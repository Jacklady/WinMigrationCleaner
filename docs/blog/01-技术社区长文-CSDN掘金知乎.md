# 换了主板/重装系统后，设备管理器里那些"幽灵设备"我终于找到批量清理的办法了

> 适用读者：折腾过换主板不重装系统、整机迁移克隆系统盘、笔记本换台式机的 Windows 用户。
> 项目地址：https://github.com/Jacklady/WinMigrationCleaner （Gitee 镜像：https://gitee.com/Jacklady/WinMigrationCleaner ）

## 起因

前阵子把系统盘从一台联想笔记本（Ryzen 7 5800H + RTX 3070）直接迁到了 Intel 台式机（Ultra 5 + RTX 4060）。系统奇迹般地起来了，但用久了总觉得不对劲：

- 设备管理器打开「显示隐藏的设备」，**296 个灰色的"幽灵设备"**——全是旧笔记本留下的硬件记录：旧指纹仪、旧摄像头、Tobii 眼动仪、旧无线网卡……
- `services.msc` 里躺着 **5 个联想/Tobii 的厂商服务**，对应的硬件早就不存在了，却还在后台跑；
- `C:\Windows\System32\DriverStore\FileRepository` 里堆了 **23 个过期驱动包**，占着好几百 MB，删又不敢手删。

手动一个个清理？设备管理器不支持批量，pnputil 命令行又容易误删在用的驱动。于是我写了个工具把这些活儿自动化了——**WinMigrationCleaner**，今天开源出来。

## 它做什么

三类迁移残留，一站式检测 + 清理：

| 模块 | 说明 |
|---|---|
| 🔍 幽灵设备 | 扫描全部设备记录，标记 `Present=false` 的残留记录，默认勾选、批量移除 |
| ⚙ 系统服务 | 按厂商关键字（联想/Tobii/AMD/Intel RST…）识别旧硬件遗留服务，可禁用可删除 |
| ⛁ 驱动包 | 扫描 DriverStore 中未被在线设备引用的驱动包，智能分级：`可清理` / `软件` / `保留` |

还有仪表盘一键扫描、一键清理安全项、导出报告等。

## 为什么敢删（安全设计是核心卖点）

清理系统级的东西，怕的就是误删。这个工具的护栏：

1. **驱动包智能分级，绝不自动删**：
   - `可清理`：无在线设备引用 + 非当前平台核心套件（显卡/无线/蓝牙）+ 非已知软件驱动（Npcap/VMware/远程工具/打印机…），三个条件同时满足才允许勾选；
   - `软件`：属于已安装软件的驱动包，默认排除——Npcap、VMware USB 这类"未运行时确实无引用"，按引用关系判断会被误伤；
   - `保留`：当前平台核心组件，直接锁定。
2. **只碰 `Present=false` 的设备记录**——正在工作的硬件永远不出现在可勾选列表里。
3. **删除失败不吞错**：pnputil 的原始错误会被翻译成人话。比如打印机驱动删不掉（`0xE0000205`），软件会告诉你"被打印后台占用"，并提供「停止 Print Spooler → 删除 → 自动重启服务」的一键流程。
4. **幂等可重复**：重复运行安全，重新插入的硬件 Windows 会自动重装驱动。
5. **零第三方二进制**：只用 Windows 内置的 WMI 和 pnputil，不装任何驱动级组件。

## 体验上的细节（v1.2）

- 启动自动扫描 + 扫描结果本地缓存（7 天内秒开），不用每次干等；
- 每项删除成功，角标/计数/列表即时更新；
- 列表实时搜索（Ctrl+F）、点列头排序、Ctrl+A 全选、F5 重扫；
- 活动日志全量记录，双击即可复制失败原因；
- 未提权也能完整扫描，点清理时引导一键提权重启。

## 下载与使用

1. 到 [Releases](https://github.com/Jacklady/WinMigrationCleaner/releases) 下载 `WinMigrationCleaner.exe`（自包含单文件约 120 MB，无需安装 .NET）；
2. 双击运行（不提权也能扫），等它扫完；
3. 仪表盘点「一键清理安全项」，或者进各页签按需处理；
4. 建议操作前建个系统还原点，清理后重启一次。

源码构建：`git clone` 后在 `src` 目录 `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../dist`（需要 .NET 9 SDK）。

## 技术栈与实现要点

C# / .NET 9 WPF，纯 code-behind 小项目，无第三方 UI 库（Fluent 风格是手写的样式资源）：

- 设备枚举：`pnputil /enum-devices`（含连接状态解析），移除用 `pnputil /remove-device`；
- 服务识别：WMI `Win32_Service` + 厂商关键字匹配；
- 驱动包：`pnputil /enum-drivers` 与 `/enum-devices /connected` 交叉比对出"无引用包"，再做软件白名单与平台套件过滤；
- 扫描缓存：结果序列化到 `%LOCALAPPDATA%`，7 天有效期。

## 求反馈

MIT 开源，欢迎 Issue/PR。厂商关键字和软件白名单在 `Scanner.cs` 顶部，补充关键字就能识别更多残留——这块特别需要大家的真实数据来完善。

- GitHub：https://github.com/Jacklady/WinMigrationCleaner
- Gitee：https://gitee.com/Jacklady/WinMigrationCleaner

如果帮到了你，给个 Star 就是对开源作者最大的鼓励 🙏
