using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace SwKit.Core
{
    /// <summary>
    /// 导出选项（STL 精度、STEP 协议等）是 SolidWorks 的全局用户设置。
    /// 修改前先记下原值，导出结束后全部恢复，不改变用户自己的 SolidWorks 设置。
    /// </summary>
    sealed class PrefSnapshot
    {
        readonly ISldWorks _sw;
        readonly Dictionary<int, bool> _toggles = new Dictionary<int, bool>();
        readonly Dictionary<int, int> _ints = new Dictionary<int, int>();
        readonly Dictionary<int, double> _doubles = new Dictionary<int, double>();

        public PrefSnapshot(ISldWorks sw) => _sw = sw;

        public void SetToggle(int id, bool value)
        {
            if (!_toggles.ContainsKey(id)) _toggles[id] = _sw.GetUserPreferenceToggle(id);
            _sw.SetUserPreferenceToggle(id, value);
        }

        public void SetInt(int id, int value)
        {
            if (!_ints.ContainsKey(id)) _ints[id] = _sw.GetUserPreferenceIntegerValue(id);
            _sw.SetUserPreferenceIntegerValue(id, value);
        }

        public void SetDouble(int id, double value)
        {
            if (!_doubles.ContainsKey(id)) _doubles[id] = _sw.GetUserPreferenceDoubleValue(id);
            _sw.SetUserPreferenceDoubleValue(id, value);
        }

        public void Restore()
        {
            foreach (var kv in _toggles) try { _sw.SetUserPreferenceToggle(kv.Key, kv.Value); } catch { }
            foreach (var kv in _ints) try { _sw.SetUserPreferenceIntegerValue(kv.Key, kv.Value); } catch { }
            foreach (var kv in _doubles) try { _sw.SetUserPreferenceDoubleValue(kv.Key, kv.Value); } catch { }
        }
    }
}
