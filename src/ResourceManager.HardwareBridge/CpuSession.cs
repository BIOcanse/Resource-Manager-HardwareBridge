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
/// 本产品限定使用 PawnIO，不装载 InpOut 或 WinIo；签名模块不可用就报告不可用。
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

    /// <summary>
    /// 电流墙的位置。本机（表版本 0x00540208）实测 [6]=85、[8]=125，
    /// 和这颗处理器的 TDC / EDC 额定值对得上。
    /// </summary>
    private const int TdcLimitIndex = 6;
    private const int EdcLimitIndex = 8;

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

    /// <summary>
    /// PM table 的前若干项，**原样**给出来。
    ///
    /// 各代处理器的表布局不一样，照着别家的索引表硬套会读出一个看着像功耗、
    /// 其实是别的东西的数。要接新字段之前先用这个把表倒出来，
    /// 和已知量（STAPM 上限、当前包功耗……）对上号再说。
    /// </summary>
    public IReadOnlyDictionary<string, object?> DumpPowerTable(int offset, int count)
    {
        var table = ReadTable() ?? throw new InvalidOperationException("读不到 PM table。");
        var start = Math.Clamp(offset, 0, Math.Max(0, table.Length - 1));
        var take = Math.Clamp(count, 1, table.Length - start);
        var values = new double[take];
        for (var index = 0; index < take; index++)
        {
            values[index] = table[start + index];
        }
        return new Dictionary<string, object?>
        {
            ["length"] = table.Length,
            ["offset"] = start,
            ["values"] = values
        };
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
            // PM table 的版本号。表布局按它区分 —— 主程序那边的索引表就是拿它当键的。
            ["powerTableVersion"] = cpu.GetTableVersion().TableVersion,
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
            ["tdcLimitAmps"] = table[TdcLimitIndex],
            ["edcLimitAmps"] = table[EdcLimitIndex],
            ["curveOptimizerCounts"] = ReadCurveOptimizer(),
            ["integratedGpuCurveOptimizerCounts"] = ReadIntegratedGpuCurveOptimizer(),
            ["maxBoostFrequencyMhz"] = ReadMaxBoostFrequency()
        };
    }

    /// <summary>
    /// 这颗处理器的**加速频率上限**，单位 MHz。
    ///
    /// Windows 那边给不出这个数：<c>Win32_Processor.MaxClockSpeed</c>、
    /// PDH 的 <c>Processor Frequency</c>、注册表 <c>~MHz</c> 报的都是**标称频率**
    /// （本机三个都是 2401），而 <c>% of Maximum Frequency</c> 比的是电源计划
    /// 当前允许的上限，不是加速上限。
    ///
    /// 所以问 SMU。读不到就是 null —— 调用方据此如实说"没有参考值"，
    /// 不拿标称频率冒充加速频率。
    /// </summary>
    private double? ReadMaxBoostFrequency()
    {
        try
        {
            var value = cpu?.GetFMax() ?? 0;
            // 0 表示这一代处理器的 RSMU 表里没有这条命令；
            // 超出常识范围的值说明读到的不是频率。
            return value is > 0 and < 10_000 ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 一条功耗上限。<paramref name="which"/> 是 stapm / slow / fast 之一。
    ///
    /// 写哪几条由主程序决定 —— 它们各有各的含义（持续、长时、瞬时），
    /// 替调用方"顺手都写了"就是把策略埋在这一侧。
    /// </summary>
    public IReadOnlyDictionary<string, object?> SetPowerLimit(string which, SmuValue value)
    {
        var target = Require();
        // 这一条只收瓦。收到别的量纲说明上游搞错了项，不是我们该替它换算的事。
        var watts = value.In(SmuUnits.Watt);

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
        // 命令号和它收的量纲一起挑出来 —— 分开写就会出现"改了命令忘了改量纲"。
        // MP1 这几条收**毫瓦**；下面 ZenStates 的通用接口收**瓦**，是两回事。
        var (mp1Message, mp1Unit) = which switch
        {
            "stapm" => (target.smu.Mp1Smu.SMU_MSG_SetStapmLimit, SmuUnits.Milliwatt),
            "slow" => (target.smu.Mp1Smu.SMU_MSG_SetSlowLimit, SmuUnits.Milliwatt),
            "fast" => (target.smu.Mp1Smu.SMU_MSG_SetFastLimit, SmuUnits.Milliwatt),
            _ => throw new ArgumentException($"不认识的功耗项：{which}。")
        };

        SMU.Status status;
        if (mp1Message != 0)
        {
            status = SendMp1(target, mp1Message, value, mp1Unit);
        }
        else
        {
            // 它的通用接口收的是**瓦**，内部自己乘 1000。
            var whole = (uint)Math.Round(watts);
            status = which switch
            {
                "stapm" => target.SetStapmLimit(whole),
                "slow" => target.SetSlowLimit(whole),
                _ => target.SetFastLimit(whole)
            };
        }

        // 只回原始状态码和回读值。算不算写成功，主程序自己比。
        var table = ReadTable();
        return new Dictionary<string, object?>
        {
            ["status"] = status.ToString(),
            ["mailbox"] = mp1Message != 0 ? "mp1" : "library",
            // 发出去的到底是什么，原样回报 —— 主程序不该靠参数名反推量纲。
            ["wroteNumber"] = value.Number,
            ["wroteUnit"] = value.Unit,
            ["sentAsUnit"] = mp1Message != 0 ? mp1Unit : SmuUnits.Watt,
            ["stapmLimitWatts"] = table?[StapmLimitIndex],
            ["fastLimitWatts"] = table?[FastLimitIndex],
            ["slowLimitWatts"] = table?[SlowLimitIndex]
        };
    }

    /// <summary>
    /// 一条电流墙。<paramref name="which"/> 是 tdc-vdd / tdc-soc / edc-vdd / edc-soc 之一。
    ///
    /// **和功耗墙一样走 MP1 优先。** 这一代移动端上 RSMU 会回一个假的 OK，
    /// ZenStates 的通用路径「先 RSMU，成功就不试 MP1」于是永远轮不到真正管用的那条。
    /// 命令号仍然用它维护的表，只是换一个邮箱。
    /// </summary>
    public IReadOnlyDictionary<string, object?> SetCurrentLimit(string which, SmuValue value)
    {
        var target = Require();
        var amps = value.In(SmuUnits.Ampere);
        // MP1 这几条收**毫安**；下面 ZenStates 的通用接口收**安**。
        var (mp1Message, mp1Unit) = which switch
        {
            "tdc-vdd" => (target.smu.Mp1Smu.SMU_MSG_SetTDCVDDLimit, SmuUnits.Milliampere),
            "tdc-soc" => (target.smu.Mp1Smu.SMU_MSG_SetTDCSocLimit, SmuUnits.Milliampere),
            "edc-vdd" => (target.smu.Mp1Smu.SMU_MSG_SetEDCVDDLimit, SmuUnits.Milliampere),
            "edc-soc" => (target.smu.Mp1Smu.SMU_MSG_SetEDCSocLimit, SmuUnits.Milliampere),
            _ => throw new ArgumentException($"不认识的电流项：{which}。")
        };

        SMU.Status status;
        if (mp1Message != 0)
        {
            status = SendMp1(target, mp1Message, value, mp1Unit);
        }
        else
        {
            // 它的通用接口收的是**安**，内部自己乘 1000。
            var whole = (uint)Math.Round(amps);
            status = which switch
            {
                "tdc-vdd" => target.SetTDCVDDLimit(whole),
                "tdc-soc" => target.SetTDCSOCLimit(whole),
                "edc-vdd" => target.SetEDCVDDLimit(whole),
                _ => target.SetEDCSOCLimit(whole)
            };
        }

        var table = ReadTable();
        return new Dictionary<string, object?>
        {
            ["status"] = status.ToString(),
            ["mailbox"] = mp1Message != 0 ? "mp1" : "library",
            ["wroteNumber"] = value.Number,
            ["wroteUnit"] = value.Unit,
            ["sentAsUnit"] = mp1Message != 0 ? mp1Unit : SmuUnits.Ampere,
            ["tdcLimitAmps"] = table?[TdcLimitIndex],
            ["edcLimitAmps"] = table?[EdcLimitIndex]
        };
    }

    /// <summary>
    /// 温度墙（Tctl 上限，摄氏度）。
    ///
    /// **这一条没有回读。** 本机的 PM table 里还没认出哪一项是它 ——
    /// 与其挑一个看着像温度的数当成上限报回去，不如如实说没有回读值，
    /// 由主程序决定要不要信这个状态码。认出来之后再补。
    /// </summary>
    public IReadOnlyDictionary<string, object?> SetTemperatureLimit(SmuValue value)
    {
        var target = Require();
        // 这一条走 ZenStates 自己的通用接口，收的是**摄氏度**，不是毫摄氏度。
        var status = target.SetTctlMax(value.AsUInt32(SmuUnits.Celsius));
        return new Dictionary<string, object?>
        {
            ["status"] = status.ToString(),
            ["wroteNumber"] = value.Number,
            ["wroteUnit"] = value.Unit,
            ["sentAsUnit"] = SmuUnits.Celsius,
            ["temperatureLimitCelsius"] = null
        };
    }

    /// <summary>
    /// PBO 标量（1~10 倍）。
    ///
    /// 它放宽的是固件那套可靠性预算（FIT）—— 倍数越大，固件越肯让电压和频率
    /// 长时间待在高处。1 倍就是厂商默认，也就是不放宽。
    /// </summary>
    public IReadOnlyDictionary<string, object?> SetPowerBoostScalar(SmuValue value)
    {
        var target = Require();
        var status = target.SetPBOScalar(value.AsUInt32(SmuUnits.Multiplier));
        return new Dictionary<string, object?>
        {
            ["status"] = status.ToString(),
            ["wroteNumber"] = value.Number,
            ["wroteUnit"] = value.Unit,
            ["sentAsUnit"] = SmuUnits.Multiplier
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

    /// <summary>
    /// 往 MP1 邮箱发一条带数值的命令。**所有走 MP1 的写入都必须经过这里。**
    ///
    /// **收的是带单位的值，不是裸数字。** 主程序那边每条设定本来就带着单位
    /// （统一的 ControlUnits：W / A / °C / 档 / ×），这里就接着用同一个单位，
    /// 由这一处把它换算成 MP1 要的毫单位。
    ///
    /// 先前的做法是参数名带量纲（<c>watts</c>、<c>amps</c>）、值是裸数字、
    /// 每个调用点自己 `* 1000`。参数名写错不会报错，漏乘也不会报错 ——
    /// 于是"把电流墙设成 85 A"安静地变成 0.085 A，处理器掉到二十几瓦，
    /// 回执一路正常。**量纲必须跟着值走，并且只在一处换算。**
    ///
    /// 量纲由**每条命令自己声明**（见各处挑命令号的那个 switch），不是这里默认 ——
    /// 命令号和量纲分开写，就会出现"改了命令忘了改量纲"。
    ///
    /// 换不过来就抛，不猜。**不在这里限制数值范围**：能调到多少是用户的事，
    /// 由主程序那边的 ControlNumberRange 表达；这一层只负责把量纲送对。
    /// </summary>
    private static SMU.Status SendMp1(Cpu target, uint message, SmuValue value, string unit)
    {
        var arguments = Utils.MakeCmdArgs(value.AsUInt32(unit));
        return target.smu.SendMp1Command(message, ref arguments);
    }

    public void Dispose() => cpu?.Dispose();
}
