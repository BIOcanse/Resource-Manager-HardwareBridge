// Resource Manager Hardware Bridge
// Copyright (C) 2026  Resource Manager contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using ResourceManager.HardwareBridge;

// Packaging smoke check exits before any hardware discovery or session is opened.
if (args.Length == 1 && args[0] == "--runtime-check")
{
    Console.WriteLine("{\"runtimeReady\":true}");
    return;
}

// 标准输入一行一条请求，标准输出一行一条应答。
// 父进程关掉标准输入，这个进程就结束 —— 生命周期跟着主程序，不留后台。
using var session = new CpuSession();

string? line;
while ((line = Console.ReadLine()) is not null)
{
    // 有的调用方会在第一行前面带上 BOM，容错掉 —— 为一个字节的编码习惯
    // 让整条通道在第一句就断掉，不值得。
    line = line.Trim().TrimStart('﻿', '​');
    if (line.Length == 0)
    {
        continue;
    }

    Console.WriteLine(JsonSerializer.Serialize(Handle(session, line)));
    Console.Out.Flush();
}

static Dictionary<string, object?> Handle(CpuSession session, string line)
{
    long id = 0;
    try
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        id = request.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
        var operation = request.TryGetProperty("op", out var opElement)
            ? opElement.GetString()
            : null;

        return operation switch
        {
            "describe" => Ok(id, session.Describe()),
            "read" => Ok(id, session.Read()),
            // 诊断用：把 PM table 原样倒出来，用来确认这一代处理器的表布局。
            "dump-power-table" => Ok(id, session.DumpPowerTable(
                request.TryGetProperty("offset", out var offsetElement)
                    ? (int)offsetElement.GetDouble()
                    : 0,
                request.TryGetProperty("count", out var countElement)
                    ? (int)countElement.GetDouble()
                    : 32)),
            "set-power-limit" => Ok(id, session.SetPowerLimit(
                Text(request, "which"),
                Value(request))),
            "set-current-limit" => Ok(id, session.SetCurrentLimit(
                Text(request, "which"),
                Value(request))),
            "set-temperature-limit" => Ok(id, session.SetTemperatureLimit(Value(request))),
            "set-pbo-scalar" => Ok(id, session.SetPowerBoostScalar(Value(request))),
            // 档位是无量纲的整数，但一样要带着单位过来 —— 统一一条规矩，
            // 不留"这一条可以不带"的例外，例外就是下一次搞错的入口。
            "set-curve-optimizer" => Ok(id, session.SetCurveOptimizer(
                (int)Value(request).In(SmuUnits.Step))),
            "set-igpu-curve-optimizer" => Ok(id, session.SetIntegratedGpuCurveOptimizer(
                (int)Value(request).In(SmuUnits.Step))),
            _ => Error(id, $"不认识的操作：{operation}")
        };
    }
    catch (Exception error)
    {
        // 这个进程是主程序的一条手臂，不该因为一条坏请求就断掉。
        // 如实把失败回过去，继续听下一条。
        return Error(id, error.Message);
    }
}

static string Text(JsonElement request, string name)
    => request.TryGetProperty(name, out var element) && element.GetString() is { } value
        ? value
        : throw new ArgumentException($"这条请求缺少 {name}。");

/// <summary>
/// 一条写入请求里的那个值，**连同它的单位**。
///
/// 单位是必填的：没有单位的数字到了 SMU 那一侧就只能靠参数名猜量纲，
/// 而猜错不会报错，只会安静地改坏硬件。主程序那边每条设定本来就带着单位，
/// 照原样传过来即可。
/// </summary>
static SmuValue Value(JsonElement request)
    => new(Number(request, "value"), Text(request, "unit"));

static double Number(JsonElement request, string name)
    => request.TryGetProperty(name, out var element)
        ? element.GetDouble()
        : throw new ArgumentException($"这条请求缺少 {name}。");

static Dictionary<string, object?> Ok(long id, IReadOnlyDictionary<string, object?> payload)
{
    var result = new Dictionary<string, object?> { ["id"] = id, ["ok"] = true };
    foreach (var (key, value) in payload)
    {
        result[key] = value;
    }
    return result;
}

static Dictionary<string, object?> Error(long id, string message)
    => new() { ["id"] = id, ["ok"] = false, ["error"] = message };
