// Resource Manager Hardware Bridge
// Copyright (C) 2026  Resource Manager contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using ZenStates.Core;
using ZenStates.Core.Drivers;

namespace ResourceManager.HardwareBridge;

/// <summary>
/// 和处理器打交道的那一层。所有硬件写入都在这里，全部经由 ZenStates-Core。
///
/// **不自己拼 SMU 邮箱、不自己编码 Curve Optimizer 的负值。**
/// 各代处理器的邮箱地址、命令号、参数编码，ZenStates-Core 都在维护，
/// 而且它会按机器情况自己选内核（开着内存完整性的机器上走 PawnIO，
/// 因为 WinRing0 在那些机器上被驱动阻止列表挡着，根本加载不了）。
/// 电压这类东西调错代价很大，照着二手资料猜编码是不能接受的。
///
/// **这里只暴露操作，不做判断。** 写哪几项、算不算成功、越界怎么办、
/// 撤销要恢复到哪儿 —— 都是主程序那边的事。这一侧回的是原始状态码和回读值，
/// 不替调用方下结论：两边各存一份策略，迟早会对不上。
/// </summary>
internal sealed class CpuSession : IDisposable
{
    /// <summary>
    /// PM table 里这几项的位置，对所有表版本都一样。
    /// 0 是持续功耗**上限**，1 是**实际**功耗 —— 上限和实际调度是两回事，
    /// 固件会按温度自行调度，所以拿实际值当写入结果是错的。
    /// </summary>
    private const int StapmLimitIndex = 0;
    private const int StapmValueIndex = 1;
    private const int FastLimitIndex = 2;
    private const int SlowLimitIndex = 4;

    private readonly Cpu? cpu;
    private readonly string? unavailableReason;

    public CpuSession()
    {
        try
        {
            cpu = new Cpu();
            if (cpu.Status is not (IODriver.LibStatus.OK or IODriver.LibStatus.PARTIALLY_OK))
            {
                unavailableReason = $"内核访问不可用：{cpu.Status}。";
                cpu = null;
            }
        }
        catch (Exception error)
        {
            unavailableReason = error.Message;
            cpu = null;
        }
    }

    /// <summary>这台机器上有什么、能设什么。主程序据此决定界面上出现哪几项。</summary>
    public IReadOnlyDictionary<string, object?> Describe()
    {
        if (cpu is null)
        {
            return new Dictionary<string, object?>
            {
                ["available"] = false,
                ["reason"] = unavailableReason
            };
        }

        return new Dictionary<string, object?>
        {
            ["available"] = true,
            ["cpuName"] = cpu.info.cpuName,
            ["codeName"] = cpu.info.codeName.ToString(),
            ["status"] = cpu.Status.ToString(),
            ["smuVersion"] = cpu.smu.Version,
            ["powerTable"] = ReadTable() is not null,
            // 命令号为 0 表示这代处理器上没有这条命令。
            ["curveOptimizer"] = cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0
                || cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0,
            ["integratedGpuCurveOptimizer"] = cpu.smu.Rsmu.SMU_MSG_SetGpuPsmMargin != 0
                || cpu.smu.Mp1Smu.SMU_MSG_SetGpuPsmMargin != 0
        };
    }

    public IReadOnlyDictionary<string, object?> Read()
    {
        var table = ReadTable() ?? throw new InvalidOperationException("读不到 PM table。");
        return new Dictionary<string, object?>
        {
            ["stapmLimitWatts"] = table[StapmLimitIndex],
            ["stapmValueWatts"] = table[StapmValueIndex],
            ["fastLimitWatts"] = table[FastLimitIndex],
            ["slowLimitWatts"] = table[SlowLimitIndex],
            ["curveOptimizerCounts"] = ReadCurveOptimizer(),
            ["integratedGpuCurveOptimizerCounts"] = ReadIntegratedGpuCurveOptimizer()
        };
    }

    /// <summary>
    /// 一条功耗上限。<paramref name="which"/> 是 stapm / slow / fast 之一。
    ///
    /// 写哪几条由主程序决定 —— 它们各有各的含义（持续、长时、瞬时），
    /// 替调用方"顺手都写了"就是把策略埋在这一侧。
    /// </summary>
    public IReadOnlyDictionary<string, object?> SetPowerLimit(string which, double watts)
    {
        var target = Require();

        /*
         * 直接发 MP1。
         *
         * ZenStates 的通用路径是「先发 RSMU，OK 就不再试 MP1」，而 Zen4 移动端上
         * RSMU 会回一个假的 OK（SetStapmLimit 的 RSMU 命令号在 Zen4 表里干脆是 0），
         * 于是真正管用的 MP1 永远轮不到 —— 实测写完 PM table 纹丝不动。
         *
         * 命令号仍然用它维护的表（Mp1Smu.SMU_MSG_*），只是换一个邮箱，不硬写常量。
         * 表里没有这条命令时才退回它的通用接口。
         */
        var mp1Message = which switch
        {
            "stapm" => target.smu.Mp1Smu.SMU_MSG_SetStapmLimit,
            "slow" => target.smu.Mp1Smu.SMU_MSG_SetSlowLimit,
            "fast" => target.smu.Mp1Smu.SMU_MSG_SetFastLimit,
            _ => throw new ArgumentException($"不认识的功耗项：{which}。")
        };

        SMU.Status status;
        if (mp1Message != 0)
        {
            var arguments = Utils.MakeCmdArgs((uint)Math.Round(watts * 1000));
            status = target.smu.SendMp1Command(mp1Message, ref arguments);
        }
        else
        {
            // 它的通用接口收的是**瓦**，内部自己乘 1000。
            var value = (uint)Math.Round(watts);
            status = which switch
            {
                "stapm" => target.SetStapmLimit(value),
                "slow" => target.SetSlowLimit(value),
                _ => target.SetFastLimit(value)
            };
        }

        // 只回原始状态码和回读值。算不算写成功，主程序自己比。
        var table = ReadTable();
        return new Dictionary<string, object?>
        {
            ["status"] = status.ToString(),
            ["mailbox"] = mp1Message != 0 ? "mp1" : "library",
            ["stapmLimitWatts"] = table?[StapmLimitIndex],
            ["fastLimitWatts"] = table?[FastLimitIndex],
            ["slowLimitWatts"] = table?[SlowLimitIndex]
        };
    }

    /// <summary>
    /// 全核 Curve Optimizer 偏移。负数是降压。
    ///
    /// 负值怎么编码由 ZenStates-Core 负责 —— 这正是不能自己猜的那一部分。
    /// </summary>
    public IReadOnlyDictionary<string, object?> SetCurveOptimizer(int counts)
    {
        var target = Require();
        var accepted = target.SetPsmMarginAllCores(counts);
        return new Dictionary<string, object?>
        {
            ["accepted"] = accepted,
            ["curveOptimizerCounts"] = ReadCurveOptimizer()
        };
    }

    public IReadOnlyDictionary<string, object?> SetIntegratedGpuCurveOptimizer(int counts)
    {
        var target = Require();
        var accepted = target.SetGpuPsmMargin(counts);
        return new Dictionary<string, object?>
        {
            ["accepted"] = accepted,
            ["integratedGpuCurveOptimizerCounts"] = ReadIntegratedGpuCurveOptimizer()
        };
    }

    private float[]? ReadTable()
    {
        if (cpu?.powerTable is null)
        {
            return null;
        }
        return cpu.RefreshPowerTable() == SMU.Status.OK
            && cpu.powerTable.Table is { } table
            && table.Length > SlowLimitIndex
                ? table
                : null;
    }

    private int? ReadCurveOptimizer()
    {
        try
        {
            // 全核设的是同一个值，所以读 0 号核就够了。
            return cpu?.GetPsmMarginSingleCore(cpu.MakeCoreMask()) is { } raw
                ? unchecked((int)raw)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private int? ReadIntegratedGpuCurveOptimizer()
    {
        try
        {
            return cpu?.GetGpuPsmMargin(cpu.MakeCoreMask()) is { } raw
                ? unchecked((int)raw)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Cpu Require()
        => cpu ?? throw new InvalidOperationException(unavailableReason ?? "内核访问不可用。");

    public void Dispose() => cpu?.Dispose();
}
