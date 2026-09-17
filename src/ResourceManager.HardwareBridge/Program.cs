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
            "set-power-limit" => Ok(id, session.SetPowerLimit(
                Text(request, "which"),
                Number(request, "watts"))),
            "set-curve-optimizer" => Ok(id, session.SetCurveOptimizer((int)Number(request, "counts"))),
            "set-igpu-curve-optimizer" =>
                Ok(id, session.SetIntegratedGpuCurveOptimizer((int)Number(request, "counts"))),
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
