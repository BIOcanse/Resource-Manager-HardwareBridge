# Resource Manager Hardware Bridge

Resource Manager 的硬件写入辅助进程。

## 这个仓库为什么单独存在

它链接 [ZenStates-Core](https://github.com/irusanov/ZenStates-Core)，那是 **GPL-3.0**。
Resource Manager 主程序是 Apache-2.0，直接引用会把主程序整个传染成 GPL-3.0。

所以把用到 ZenStates-Core 的代码全部放进**这个独立程序**：它自己按 GPL-3.0 发布，
主程序通过标准输入输出上的一行一条 JSON 跟它说话 —— 两个独立程序，各用各的许可证。

顺带的好处是隔离：碰内核的代码单独一个进程，崩了不拖累主程序。

## 协议

标准输入一行一条 JSON 请求，标准输出一行一条 JSON 应答，`id` 原样带回。

```json
{"id":1,"op":"describe"}
{"id":2,"op":"read"}
{"id":3,"op":"set-power-limit","which":"stapm","watts":60}
{"id":4,"op":"set-curve-optimizer","counts":-10}
{"id":5,"op":"set-igpu-curve-optimizer","counts":-10}
```

应答固定形如 `{"id":N,"ok":true,...}` 或 `{"id":N,"ok":false,"error":"..."}`。

**这一侧只暴露操作，不做判断。** 写哪几项、算不算成功、越界怎么办、撤销恢复到哪儿，
都是主程序的事；这里回的是原始 SMU 状态码和写完之后的回读值，不替调用方下结论。
两边各存一份策略，迟早会对不上。

父进程关掉标准输入，这个进程就退出 —— 生命周期跟着主程序走，不留后台。

## 需要什么

- 管理员权限（主程序本来就是提权运行的，子进程继承）。
- 内核访问由 ZenStates-Core 自己选择。在开着内存完整性（HVCI）的机器上
  它会走 PawnIO —— WinRing0 在那些机器上被驱动阻止列表挡住，根本加载不了。

## 构建

```
dotnet build src/ResourceManager.HardwareBridge/ResourceManager.HardwareBridge.csproj -c Release
```

ZenStates-Core 以 git 子模块的形式引用，克隆后先 `git submodule update --init`。
