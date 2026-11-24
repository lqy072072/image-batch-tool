using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// 相册的用户数据层：收藏/评分/旗标/隐藏/标签、自定义相册、智能相册、人物名称、保存搜索、私密相册。
// 独立于扫描索引，避免升级时破坏 alb_index.dat；只保存被用户标记过的照片，空库时文件极小。
public sealed class AlbumMeta
{
    readonly object gate = new object();
    readonly string file;
    Dictionary<string, object> state;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public AlbumMeta(string path)
    {
        file = path;
        Load();
    }

    static Dictionary<string, object> Dict(object o)
    {
        var d = o as Dictionary<string, object>;
        return d ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    static List<object> List(object o)
    {
        var l = o as List<object>;
        if (l != null) return l;
        var res = new List<object>();
        var en = o as IEnumerable;
        if (en != null && !(o is string))
            foreach (var x in en) res.Add(x);
        return res;
    }

    static object ArrayState(object o)
    {
        if (o is List<object>) return o;
        if (o is IEnumerable && !(o is string)) return List(o);
        return new List<object>();
    }

    public static string Key(string path)
    {
        return (path ?? "").Trim().ToLowerInvariant();
    }

    void Normalize()
    {
        if (state == null) state = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!state.ContainsKey("items") || !(state["items"] is Dictionary<string, object>))
            state["items"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!state.ContainsKey("albums") || !(state["albums"] is IEnumerable) || state["albums"] is string) state["albums"] = new List<object>();
        else state["albums"] = ArrayState(state["albums"]);
        if (!state.ContainsKey("searches") || !(state["searches"] is IEnumerable) || state["searches"] is string) state["searches"] = new List<object>();
        else state["searches"] = ArrayState(state["searches"]);
        if (!state.ContainsKey("saved") || !(state["saved"] is IEnumerable) || state["saved"] is string) state["saved"] = new List<object>();
        else state["saved"] = ArrayState(state["saved"]);
        if (!state.ContainsKey("people") || !(state["people"] is Dictionary<string, object>))
            state["people"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var people = Dict(state["people"]);
        if (!people.ContainsKey("names") || !(people["names"] is Dictionary<string, object>))
            people["names"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!people.ContainsKey("hidden") || !(people["hidden"] is IEnumerable) || people["hidden"] is string) people["hidden"] = new List<object>();
        else people["hidden"] = ArrayState(people["hidden"]);
        if (!state.ContainsKey("private") || !(state["private"] is Dictionary<string, object>))
            state["private"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    void Load()
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file, Encoding.UTF8);
                    state = json.DeserializeObject(text) as Dictionary<string, object>;
                }
            }
            catch { state = null; }
            Normalize();
        }
    }

    void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string text = json.Serialize(state);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(file))
            {
                string bak = file + ".bak";
                try { File.Copy(file, bak, true); } catch { }
                try { File.Replace(tmp, file, null); }
                catch { File.Delete(file); File.Move(tmp, file); }
            }
            else File.Move(tmp, file);
        }
        catch { }
    }

    public string ToJson()
    {
        lock (gate) { Normalize(); return json.Serialize(state); }
    }

    public Dictionary<string, object> GetItem(string path)
    {
        lock (gate)
        {
            var items = Dict(state["items"]);
            object v;
            return items.TryGetValue(Key(path), out v) ? v as Dictionary<string, object> : null;
        }
    }

    public string ItemJson(string path, string fallback)
    {
        var d = GetItem(path);
        return d == null ? fallback : json.Serialize(d);
    }

    public void SetItem(string path, Dictionary<string, object> item)
    {
        lock (gate)
        {
            var items = Dict(state["items"]);
            string k = Key(path);
            if (item == null || item.Count == 0) items.Remove(k);
            else items[k] = item;
            SaveLocked();
        }
    }

    public void SetItems(string arrayJson)
    {
        lock (gate)
        {
            var rows = List(json.DeserializeObject(arrayJson));
            if (rows == null) return;
            var items = Dict(state["items"]);
            foreach (var row in rows)
            {
                var d = row as Dictionary<string, object>;
                if (d == null) continue;
                object pobj; string p = null;
                if (d.TryGetValue("p", out pobj)) p = pobj as string;
                if (string.IsNullOrEmpty(p)) continue;
                d.Remove("p");
                string k = Key(p);
                if (d.Count == 0) items.Remove(k); else items[k] = d;
            }
            SaveLocked();
        }
    }

    // 更新相册/搜索/人物/私密等顶层状态；items 不随前端覆盖。
    public void MergeState(string stateJson)
    {
        lock (gate)
        {
            var incoming = json.DeserializeObject(stateJson) as Dictionary<string, object>;
            if (incoming == null) return;
            foreach (var kv in incoming)
            {
                if (kv.Key == "items") continue;
                state[kv.Key] = kv.Value;
            }
            Normalize();
            SaveLocked();
        }
    }

    public string PeopleName(int gid)
    {
        lock (gate)
        {
            var p = Dict(state["people"]); var names = Dict(p["names"]);
            object v; if (names.TryGetValue(gid.ToString(), out v) && v != null) return v.ToString();
            return "";
        }
    }

    public bool PersonHidden(int gid)
    {
        lock (gate)
        {
            var p = Dict(state["people"]); var hidden = List(p["hidden"]);
            foreach (var x in hidden) { try { if (Convert.ToInt32(x) == gid) return true; } catch { } }
            return false;
        }
    }

    public void SetPersonName(int gid, string name)
    {
        lock (gate)
        {
            var p = Dict(state["people"]); var names = Dict(p["names"]);
            if (string.IsNullOrEmpty(name)) names.Remove(gid.ToString());
            else names[gid.ToString()] = name;
            SaveLocked();
        }
    }

    public void SetPersonHidden(int gid, bool hidden)
    {
        lock (gate)
        {
            var p = Dict(state["people"]); var list = List(p["hidden"]);
            list.RemoveAll(delegate(object x) { try { return Convert.ToInt32(x) == gid; } catch { return false; } });
            if (hidden) list.Add(gid);
            p["hidden"] = list;
            SaveLocked();
        }
    }
}
