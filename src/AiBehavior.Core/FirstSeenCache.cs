using System;

namespace AiBehavior.Core;

/// <summary>固定容量的首次诊断集合，保留未知角色和脑型组合，不依赖普通日志令牌。</summary>
public sealed class FirstSeenCache
{
    private readonly string?[] _names = new string?[32];
    private readonly int[] _roles = new int[32];
    private int _count;
    private bool _overflowReported;

    /// <summary>返回是否应输出首次记录；容量耗尽只再输出一次溢出提示。</summary>
    public bool ShouldReport(int role, string name, out bool overflow)
    {
        overflow = false; // 普通首次记录可直接显示完整脑型。
        for (int index = 0; index < _count; index++) // 最大只比较固定三十二项。
            if (_roles[index] == role && string.Equals(_names[index], name, StringComparison.Ordinal)) return false; // 相同组合不重复刷屏。
        if (_count == _names.Length) // 不因其他模组动态生成名称而无限分配。
        {
            if (_overflowReported) return false; // 溢出提示也只输出一次。
            _overflowReported = overflow = true; // 调用方明确说明后续组合只计数。
            return true;
        }
        _roles[_count] = role; // 保存实际原生角色值，避免合并不同兼容问题。
        _names[_count++] = name; // 复用脑型字符串，不记录档案身份。
        return true;
    }
}
