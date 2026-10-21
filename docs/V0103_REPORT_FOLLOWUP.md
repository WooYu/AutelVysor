# v0.10.2 补测报告整改（0.10.3 源码）

修复基于 main `ce1f83b`。原报告中的“未测试”“部分验证”继续有效；新增代码回归不等于真机验收。

## 已定位问题

| 问题 | 原因 / 修改 | 验证范围 |
|---|---|---|
| AV-001 命令参数丢失 | ADB 会拼接 shell 参数，原 `shell sh -c command` 的 command 缺少远端引号。改成 `shell command`，由设备 shell 解析完整命令。Shell、批量 Shell、设备详情和网络诊断共享修复。 | 真实本地 POSIX shell + ADB 子进程边界，验证参数、单引号/空格、复合命令、支持 shell_v2 的退出码、Battery/Display/Wi-Fi 分段。仍需在设备逐项对照。 |
| AV-002/003 录屏生命周期 | 原进程启动后没有保存，也没有停止入口；无窗口录屏不能通过普通窗口关闭收尾。现在每设备最多一个录屏，保留屏幕外原生窗口作为 WM_CLOSE 端点，Stop recordings / 退出先等待正常关闭，异常或超时明确报错。 | Windows 原生子进程模拟启动、防重复和正常关闭写入文件。真实 MP4 播放完整性、USB 中断和异常退出恢复仍待验收。 |
| AV-004 数字/字母不输入 | `--prefer-text` 依赖 SDL_TEXTINPUT；报告显示原生控制键路径有效。改用 `--keyboard=sdk --raw-key-events`，让数字/字母走原始按键路径。保留现有焦点转交。 | 检查实际进程启动参数及跨进程焦点。未宣称已定位 SDL 内部丢事件的具体原因。真实 SDL / Android / Windows 输入法需重测。 |
| AV-005 Auto 显示旧角度 | 原读取 SurfaceOrientation 失败后使用 user_rotation；后者是上次锁定设置，不是实际显示角度。共用 DisplayRotationReader，优先 default display 的 DisplayFrames / mRotation / mCurrentRotation，旧设备回退 SurfaceOrientation。 | 模拟实际 0°与旧锁定 90°、实际 270°、其他显示器和字段缺失。无法获取时显示 unavailable，不编造 0°。 |

## 18 项覆盖状态

| 功能 | 本次处理 | 仍需验收 |
|---|---|---|
| 全屏 / Fit / 1:1 / 隐藏侧栏 | 保留已有布局/焦点回归 | 严格逐像素比例；完整横竖屏窗口矩阵 |
| 中文 / 剪贴板 / 电脑键盘 | AV-004 路径修正；显式 clipboard / 可选 ADB IME 保留 | 普通虚拟键、特殊符号、中文 IME、双向剪贴板；不自动安装或选择 IME |
| 真机旋转同步 | AV-005 共用实际显示源；GUI 测试也移除 user_rotation 假回退 | 多设备物理转动、前台应用锁定、屏幕关闭和尺寸变化 |
| 音频路由 | 保留 configured, not a playback test 文案 | PC / 手机 / 蓝牙实际声音及切换 |
| 断线恢复 | 无新增恢复逻辑 | USB 抖动、ADB server 重启、锁屏解锁 |
| 文件管理 | 无新增逻辑 | 根目录、上传下载校验、新建、删除闭环 |
| ADB Shell | AV-001 参数 / 引号 / 退出码回归 | 真机带参数命令；仍是命令执行器，不是交互式 PTY |
| grant / revoke / appops | 无新增逻辑 | 在专用测试应用执行并恢复权限 |
| App 搜索筛选 | 保留包名搜索和 All/User/System | 分类准确性；中文应用名搜索目前不支持，不算通过 |
| Logcat | 无新增逻辑 | 构造日志，验证级别/Tag/PID/文本组合 |
| 网络调试 | AV-001 修复 Wi-Fi 等命令参数 | Ping、DNS、路由、端口逐项真机对照 |
| 设备信息 | AV-001 修复 wm / dumpsys 参数 | CPU/RAM/存储准确性；旧 Android 兼容性 |
| Bugreport | 无新增逻辑 | 生成、文件结构、失败恢复 |
| Screenshot / Recording | AV-002/003 进程跟踪、停止入口、退出收尾 | 真机 MP4 播放、历史操作、录屏失败和退出无残留 |
| 批量操作 | AV-001 修复共享 Shell 路径；保留每设备退出状态 | 多设备命令与 APK 安装闭环；现代 shell_v2 设备退出码 0 仅代表命令执行成功；旧 adbd 不能可靠传回远端退出码 |
| ADB 收藏 | 无新增逻辑 | 保存、重启持久化、执行、删除 |
| 自动更新 | 保留 GitHub 新版本检查 | 下载校验/升级/回滚未实现；不自动执行下载文件 |
| Windows 签名 | 保留已有可信证书签名流程和 FileDescription=Autel | 无可信证书时仍为 NotSigned；需公司证书/CI secrets 后才能签名验收 |

## 验证入口

- `dotnet run --project tests/BridgeRegressionTests -c Release`：跨平台直接编译生产 Bridge / ToolRunner / 旋转读取器，模拟 ADB 进程边界。
- `dotnet run --project tests/RegressionTests -c Release`：Windows WPF / 原生进程 / 录屏生命周期回归。
- `scripts/run-physical-gui-tests.ps1`：在允许运行客户端的 Windows 电脑，使用专用 fixture 真机重测；新增 AV-004 的 `12345 → Backspace,6,7 → 123467` 原生虚拟键和 AV-005 的实际角度/UI 对照。
- 公司只允许已批准 ADB / PowerShell 时，继续使用 `scripts/run-company-adb-e2e.ps1`；它不能证明被策略阻止的 Windows 客户端或 SDL 键盘已经通过。

首个仅新增回归的 Windows CI：`d81b51a`，Shell / Auto angle / keyboard launch / duplicate recording 四组均出现预期失败，其余七组通过。修复后的运行结果以 PR 检查为准。

## 审查补充

- 清理异常后只释放已确认退出的录屏；仍存活或无法查询状态的进程继续跟踪、阻止重复启动，并阻止应用关闭，允许重试。对应回归先复现“失败后 HasRecordings 误变 false”，再修复。
- API 19 等旧 adbd 不支持 shell_v2 时，主机退出码不能可靠表示设备命令退出码；本次未增加旧协议的远端退出码封装，不能据现代回归声称旧设备的 `false` 已通过。
- 已知保守提示边界：录屏恰好在发现窗口或发送 WM_CLOSE 期间成功退出，可能仍提示需要核对文件。此时不会把不确定的文件标为正常完成。
- 后续可增加独立 mRotation / mCurrentRotation、显示器顺序变化的更多夹具；当前现代旋转夹具含 DisplayFrames，旧式 SurfaceOrientation 已由原有方向切换用例覆盖。
