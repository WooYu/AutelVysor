# 公司电脑真机自动测试：仅使用现有 ADB + PowerShell

## 目标与边界

适用于公司安全策略不允许运行非公司授权 EXE，但现有公司批准的 adb.exe 和 Windows PowerShell 可以运行的 Windows 电脑。

**不下载安装任何新 EXE，不安装 GitHub Runner，不下载 scrcpy，不要求 Gradle/Java/.NET，不改执行策略，不绕过企业管控。**

可直接测试已连接的真实 Android 设备（包含部分 Android 4.4 兼容项）。不能把纯 ADB 的检查结果当作 Autel Vysor Windows 程序的全功能回归测试：WPF 投屏、PC 端键盘焦点、窗口操作、PC 音频等必须在 IT 批准运行 Autel Vysor 后另行测试。

## 1. 先确认现有 ADB

在公司 Windows PowerShell 执行：

```powershell
Get-Command adb.exe
& "C:\公司批准的路径\adb.exe" devices -l
```

如 `adb devices -l` 出现 `unauthorized`，请正常在手机上确认 USB 调试授权。设备必须显示 `device`，不能是 `offline` 或 `unauthorized`。

脚本默认会跳过并拒绝把检测到的 emulator 当作真机。

## 2. 直接运行默认安全测试

将本仓库的 `scripts/run-company-adb-e2e.ps1` 放在公司允许的脚本目录，不需要下载源码中的 EXE。使用公司允许的方式运行本地脚本，不需要修改 PowerShell ExecutionPolicy。

```powershell
powershell.exe -NoProfile -File ".\scripts\run-company-adb-e2e.ps1" -AdbPath "C:\公司批准的路径\adb.exe"
```

默认会自动遍历所有在线/已授权真实设备，执行：

- 连接及 Android 系统属性；版本、型号、ABI、分辨率、DPI、内存、存储、电池
- ADB Shell/getprop；App 系统/用户包名列表和 dumpsys package；进程列表
- 网卡、路由、DNS 提示和 127.0.0.1 本机回环 Ping（不依赖访问互联网）
- 在 /sdcard/Download 下新建**随机且唯一**的测试文件夹，上传、读取、下载并 SHA256 校验，最后清理**仅此测试文件夹**
- Android 截图并验证 PNG 文件头和尺寸，然后清理临时截图
- 生成唯一 Logcat Tag + Info 日志并确认过滤后可以查询
- Android 屏幕实际方向状态读取（dumpsys；仅 settings 值不足以判定真实旋转）
- Android dumpsys audio 元信息（**并非耳机实际有声**）

不卸载公司业务 App、不清理真实 App 数据、不重启手机、不更改旋转、不重启 ADB 服务，也不会将报告上传到 GitHub。自愿启用的 App 清数据和权限/AppOps 测试仅允许包名前缀 com.autel.vysor.fixture，防止误操作公司业务 App。

可只测一台（serial 从 `adb devices -l` 获取）：

```powershell
powershell.exe -NoProfile -File ".\scripts\run-company-adb-e2e.ps1" -AdbPath "C:\公司批准的路径\adb.exe" -Serials "YOUR_DEVICE_SERIAL"
```

## 3. 专用空闲设备的可选测试

仅在公司批准的专用实验设备上启用：

```powershell
powershell.exe -NoProfile -File ".\scripts\run-company-adb-e2e.ps1" -AdbPath "C:\公司批准的路径\adb.exe" -IncludeRotation -IncludeRecording
```

可选参数：

| 参数 | 行为 |
| --- | --- |
| `-IncludeRotation` | 临时修改真实 Android 屏幕方向，验证实际结果，`finally` 中恢复原方向/传感器设置 |
| `-IncludeRecording` | 使用 Android 内置 `screenrecord` 录 3 秒，验证 MP4 文件头，不启动 scrcpy.exe |
| `-IncludeBugreport` | 导出本地 Bugreport，可能耗时较长并包含敏感数据 |
| `-IncludeReboot` | 真实重启设备，等待最多约 150 秒恢复 ADB |
| `-IncludeAdbRestart` | 关闭并重新启动共享 ADB Server，可能中断所有 ADB 会话 |
| `-FixturePackage` | 显式指定**公司批准且已经安装**的专用测试 App；脚本**不会自动下载或安装 APK** |
| `-TestFixtureInput` | 在专用测试 App 中发送 ASCII 测试文本并以 `run-as` 验证 Android EditText；仅适用于已适配 `MainActivity / files/input.txt` 的 debuggable fixture |
| `-IncludeFixtureMutations` | 必须与前一参数共同使用，只会停止/清除/启动指定的**专用测试 App**，会删除该测试 App 的数据 |

默认不执行影响业务的操作。真实旋转测试可能受到 Android 前台应用方向锁定、厂家 ROM 政策、息屏暂停旋转传感器等限制。脚本必须读取实际方向才能给 PASS。

## 4. 报告

完成后本地生成目录：

```text
test-results/company-adb-YYYYMMDD-HHMMSS/
├── report.html             # 适合直接用浏览器查看
├── summary.json            # 完整机器可读结果
├── results.csv             # Excel / 管理后台汇总
├── adb-devices.txt
└── device-<serial>/
    ├── device.txt
    ├── network.txt
    ├── audio-route.txt
    ├── input.txt
    ├── downloaded.txt
    └── screenshot.png
```

每项结果明确标记：

- `PASS`：对应的真实 Android ADB 后台测试通过，不代表 Windows 端 UI 已通过。
- `FAIL`：真实设备操作失败、结果不符或设备不支持。
- `SKIP`：需要明确授权的可选操作，当前未启用。
- `BLOCKED_POLICY`：公司策略禁止运行 Autel Vysor / scrcpy，无法验证 Windows 程序投屏、键鼠、全屏、UI 等。
- `EXTERNAL_VALIDATION`：耳机实际是否出声、物理倾斜/拔插 USB 等，需要额外感知或专用测试硬件。

报告包含设备序列号、截图和部分系统诊断信息，只在本机保存；上传任何报告前应检查并脱敏。未审查前请勿放到公开 GitHub。

## 5. 后续如何真正覆盖 Windows 投屏界面

实现**真正的完整软件端到端真机自动化**有一个不可绕开的前提：公司 IT 通过审查，将 Autel Vysor 及其实际打包依赖加入白名单，或使用公司认可的代码签名流程发布正式版。

获准后再运行已有的 `scripts/run-physical-gui-tests.ps1`，验证 WPF UI、scrcpy 投屏、PC 输入、屏幕旋转及手势。GitHub 云端 CI、只有 ADB 的测试、或者伪造的通过报告，都不能取代此步骤。

## 6. 验证测试脚本本身

本仓库 CI 的 `Physical harness validation (NO real devices)` 会使用 Windows PowerShell 语法解析器检查此脚本；CI 本身无权访问公司设备，**绿色 CI 不等于真机 PASS**。

脚本目录内出现提示要修改执行策略、下载外部驱动或者启用管理员权限时，先由企业 IT 审批，而不是绕过策略。

## CI 烟雾测试与真机测试的区别

CI 使用 **PowerShell 编写的模拟 ADB** 测试脚本的执行逻辑、临时文件清理和 HTML/CSV/JSON 报告生成；所有结果仅证明脚本本身能运行，绝不是 Android 真机测试结果。只有公司电脑上使用公司现有 adb.exe 连接实体设备执行，才能产生真正的真机报告。

**实现说明**：PowerShell 的自动变量 `$args` 与显式参数不能重名；当前脚本统一使用 `$Arguments` 传递 ADB 命令数组，CI 模拟执行将验证参数确实传递给现有 ADB。

**CI 调用检查**：模拟 ADB 测试会记录每一条收到的设备命令，检查 PowerShell 传参是否完整；这不会访问任何公司实体设备。

CI 逻辑自测还验证端口读取以及脚本中只能存在一组 ADB 函数和退出判断，避免自动生成补丁时出现重复脚本。

CI 会输出 PowerShell 解析器的准确出错行号；未经验证的脚本不会合并到主分支。

最终还会针对新添加的权限操作进行 PowerShell 语法验证；测试操作只能指向 com.autel.vysor.fixture 系列专用包。
