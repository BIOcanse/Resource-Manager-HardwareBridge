# Resource Manager Hardware Bridge

Resource Manager 的硬件写入辅助进程。就是在 [ZenStates-Core](https://github.com/BIOcanse/ZenStates-Core)
外面包一层，把它的几个操作用 JSON 暴露出来，别的什么都不做。

## 为什么单独一个仓库

ZenStates-Core 是 **GPL-3.0**，Resource Manager 主程序是 Apache-2.0，直接引用会把主程序
整个传染成 GPL。所以用到它的代码全放进这个独立程序，自己按 GPL-3.0 发布，
主程序只通过标准输入输出跟它说话 —— 两个独立程序，各用各的许可证。

顺带的好处是隔离：碰内核的代码单独一个进程，崩了不拖累主程序。

## 用法

标准输入一行一条 JSON，标准输出一行一条 JSON，`id` 原样带回。
父进程关掉标准输入，它就退出。

需要管理员权限。内核访问由 ZenStates-Core 自己选，开着内存完整性（HVCI）的机器上走
PawnIO —— WinRing0 在那种机器上被驱动阻止列表挡着，根本加载不了。
`inpoutx64.dll` 要和可执行文件放在一起（ZenStates-Core 构造时会无条件加载它）。

## API

应答统一是 `{"id":N,"ok":true,...}` 或 `{"id":N,"ok":false,"error":"..."}`。

### `describe` — 这台机器上有什么、能设什么

```json
{"id":1,"op":"describe"}
```
```json
{"id":1,"ok":true,"available":true,"cpuName":"AMD Ryzen 9 8940HX with Radeon Graphics",
 "codeName":"DragonRange","status":"OK","smuVersion":5531904,
 "powerTable":true,"curveOptimizer":true,"integratedGpuCurveOptimizer":true}
```

后面三个布尔是"这代处理器上有没有这条 SMU 命令"。调用方据此决定界面上出现哪几项。
拿不到内核访问时回 `available:false` 加一个 `reason`。

### `read` — 读当前值

```json
{"id":2,"op":"read"}
```
```json
{"id":2,"ok":true,"stapmLimitWatts":145,"stapmValueWatts":41.37,
 "fastLimitWatts":40,"slowLimitWatts":145,
 "curveOptimizerCounts":-10,"integratedGpuCurveOptimizerCounts":0}
```

**`stapmLimitWatts` 是上限，`stapmValueWatts` 是实际功耗**，两回事 ——
固件会按温度自己调度，实际值通常远低于上限。别拿实际值当写入结果去校验。

### `set-power-limit` — 一条功耗上限

```json
{"id":3,"op":"set-power-limit","which":"stapm","watts":60}
```
`which` 是 `stapm`（持续）/ `slow`（长时）/ `fast`（瞬时）之一。一次一条，
写哪几条由调用方决定。回的是原始 SMU 状态码加上写完之后三条的回读值：

```json
{"id":3,"ok":true,"status":"OK","stapmLimitWatts":60,"fastLimitWatts":40,"slowLimitWatts":60}
```

### `set-curve-optimizer` — 全核 Curve Optimizer 偏移

```json
{"id":4,"op":"set-curve-optimizer","counts":-10}
```
```json
{"id":4,"ok":true,"accepted":true,"curveOptimizerCounts":-10}
```

单位是**档**不是伏。负数降压。负值怎么编码是 ZenStates-Core 的事，这里原样转交。

### `set-igpu-curve-optimizer` — 核显 Curve Optimizer 偏移

```json
{"id":5,"op":"set-igpu-curve-optimizer","counts":-10}
```
同上，回 `integratedGpuCurveOptimizerCounts`。

## 一条原则

**这一侧只暴露操作，不做判断。** 写哪几项、算不算成功、越界怎么办、撤销恢复到哪儿，
都是调用方的事。这里回原始状态码和回读值，不替调用方下结论 ——
两边各存一份策略，迟早会对不上。

## 构建

```
git submodule update --init
dotnet build src/ResourceManager.HardwareBridge/ResourceManager.HardwareBridge.csproj -c Release
```

## 许可

GPL-3.0-or-later。ZenStates-Core 版权归 irusanov，见子模块里的 LICENSE。
