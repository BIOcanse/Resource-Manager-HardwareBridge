// Resource Manager Hardware Bridge
// Copyright (C) 2026  Resource Manager contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace ResourceManager.HardwareBridge;

/// <summary>
/// 量纲标识。和主程序那套统一单位（ControlUnits）用**同一串字面量**，
/// 两边各写一套名字迟早对不上。
///
/// **毫安和安是两个不同的单位，各自有名字。** 不存在"这个接口默认用毫"
/// 这种隐含约定 —— 那正是上次把 85 A 发成 0.085 A 的原因。
/// 每条 SMU 命令自己声明它收哪一个，写在挑命令号的同一处。
/// </summary>
internal static class SmuUnits
{
    public const string Watt = "W";
    public const string Milliwatt = "mW";
    public const string Ampere = "A";
    public const string Milliampere = "mA";
    public const string Volt = "V";
    public const string Millivolt = "mV";
    public const string Celsius = "°C";
    public const string Megahertz = "MHz";
    public const string Step = "档";
    public const string Multiplier = "×";

    /// <summary>
    /// 能互相换的量纲对，以及换算系数。**只有这张表里有的才换，其余一律拒绝。**
    ///
    /// 表是穷举的，不是"看着像就换"：瓦换不成安，摄氏度换不成毫摄氏度。
    /// </summary>
    private static readonly (string From, string To, double Factor)[] Conversions =
    [
        (Watt, Milliwatt, 1000),
        (Milliwatt, Watt, 0.001),
        (Ampere, Milliampere, 1000),
        (Milliampere, Ampere, 0.001),
        (Volt, Millivolt, 1000),
        (Millivolt, Volt, 0.001)
    ];

    /// <summary>从一个量纲换到另一个。换不了就是 null，由调用方决定怎么报错。</summary>
    public static double? Convert(double number, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return number;
        }
        foreach (var (source, target, factor) in Conversions)
        {
            if (string.Equals(source, from, StringComparison.Ordinal)
                && string.Equals(target, to, StringComparison.Ordinal))
            {
                return number * factor;
            }
        }
        return null;
    }
}

/// <summary>
/// 一个要写进 SMU 的值，**连同它的量纲**。
///
/// 为什么不是一个裸 double：SMU 的邮箱不校验量纲，发多少收多少。
/// 裸数字只能靠参数名（<c>watts</c> / <c>amps</c>）表明自己是什么，
/// 而参数名写错、换算漏乘都不会报错 —— 实测踩过一次：
/// 「把电流墙设成 85 A」少乘一个 1000 变成 0.085 A，
/// 处理器被掐到二十几瓦，而回执一路正常。
///
/// 所以量纲跟着值走，换算只发生在 <see cref="In"/>，而且**换不了就抛**，
/// 不猜：猜错的代价是改坏真实硬件。
///
/// **这里不限制数值范围。** 量纲一旦明确，换算就是确定的，不需要再拿一个
/// 猜出来的"合理区间"去兜底；能调到多少是用户的事，由主程序那边的
/// ControlNumberRange 表达，后端不替它再限一道。
/// </summary>
internal readonly record struct SmuValue(double Number, string Unit)
{
    /// <summary>
    /// 按这条命令要的量纲取值。同类量纲之间换算（安↔毫安），
    /// 不同类的（瓦对安、摄氏度对毫安）直接抛。
    /// </summary>
    public double In(string expected)
    {
        // 只挡非数：能调到多少是用户的事，这一层不设上下限。
        // 那属于主程序那边的 ControlNumberRange，后端不替它再限一道。
        if (!double.IsFinite(Number))
        {
            throw new ArgumentException($"这个值不能写进 SMU：{Number} {Unit}。");
        }
        return SmuUnits.Convert(Number, Unit, expected)
            ?? throw new ArgumentException($"这一项要 {expected}，收到的是 {Unit}，换不过来。");
    }

    /// <summary>取整成 SMU 邮箱收的那个无符号整数。</summary>
    public uint AsUInt32(string expected) => (uint)Math.Round(In(expected));

    public override string ToString() => $"{Number:0.###} {Unit}";
}
