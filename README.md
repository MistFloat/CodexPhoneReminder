# Codex Phone Reminder

一个遵循 [PRD](./PRD.md) 的局域网 MVP：Windows 本地代理读取 Codex CLI/GUI 共享的结构化会话记录，Android 手机可通过原生 APK 或保留的 PWA 查看任务，并用 Codex CLI 恢复同一对话。

任务详情现在包含项目对话控制台：显示同一 thread 最近 200 条用户/助手消息，并可在任务非运行状态下从手机发送下一条指令。关键事件时间线保留在折叠的诊断区域。

## 快速开始

```powershell
dotnet run --project src/CodexPhoneReminder.Agent
```

1. 电脑浏览器打开 `http://localhost:5187/api/pair`，记下 6 位配对码与电脑指纹。
2. 手机和电脑连接同一局域网，在手机打开响应中的 `address`。
3. 输入配对码；浏览器菜单选择“添加到主屏幕”。

## Android APK

原生 Android 客户端位于 `android`，使用 Java、API 36、Android Gradle Plugin 9.3.0、Gradle Wrapper 9.5.0 和 JDK 17。它支持：

- 输入代理地址与 6 位配对码；设备令牌、电脑指纹和地址由 Android Keystore 加密保存。
- 查看任务列表、CLI 可用状态、工作区映射数，以及最近 200 条项目对话。
- 从手机向同一 Codex thread 发送下一条指令。
- 项目对话详情正确解析服务端的任务包装结构，并在页面打开时每 2 秒刷新消息；刷新不清空正在编辑的输入。
- 手机发送的临时消息使用 `pending:` 标识；会话扫描器读到权威 JSONL 消息后原位替换临时气泡，避免同一用户消息显示两次。
- Android 0.4.2 在收到 401 时立即清除失效令牌并返回六位码配对页，任务页也提供“重新配对”入口。
- 服务端将设备令牌摘要和撤销状态保存到 `data/paired-devices.json`；完成一次新版配对后，代理正常重启不再撤销授权。
- Android 0.5.0 仅在用户打开某个项目对话时请求结构化进度，并每 2 秒续订 8 秒服务端租约；退出详情或进入后台后租约自动过期。
- 进度接口使用 `after` 游标增量返回当前回合的工具开始/完成、文件修改、测试构建及回合完成/失败事件，不返回命令参数、工具输出或逐 token 推理。
- Android 0.5.1 的任务列表按项目文件夹分组；每个对话可经确认后归档，归档只从列表隐藏，不删除 Codex 会话历史。
- Android 0.5.2 修复项目对话列表无法滚动；对话消息轮询不再受实时进度接口失败影响，并通过禁用缓存确保回复无需重启 App 即可更新。
- Android 0.6.0 完成后台通知闭环：审批、等待回复、完成和失败按事件去重提醒，连续断线两次后提醒并在恢复时通知；点击任务通知直达对应对话，设备重启后自动恢复监听。
- Android 0.6.1 保留两秒消息同步，但仅在消息内容变化时重绘；用户正在选择对话文字时暂停消息区重绘，复制结束后自动补上最新内容。
- 以前台服务每 10 秒同步；等待批准、等待回复、完成、失败和离线状态变化触发去重通知。
- 点击任务通知进入对应对话；首次同步只建立状态基线，不重复通知历史事件。

本机构建：

```powershell
cd android
.\gradlew.bat assembleDebug
```

可安装产物位于 `android/app/build/outputs/apk/debug/app-debug.apk`。这是 Android debug key 签名的测试包；正式分发前需建立独立 release keystore，并使用 `assembleRelease` 生成发布包。

按需进度接口：

```text
GET /api/tasks/{thread-id}/progress?after=<last-event-id>
```

调用会为该对话续订短租约。任务列表、后台通知服务和未打开的对话不会调用此接口。

代理运行后，手机还可以直接从现有网页地址下载 APK：

```text
http://<电脑局域网地址>:5187/downloads/android-debug.apk
```

代理默认扫描 `%USERPROFILE%\.codex\sessions`。GUI 创建的用户对话会按 thread/session ID 自动出现；内部子代理会话被过滤。状态持久化在 `src/CodexPhoneReminder.Agent/data`，该目录不应提交。

## Codex CLI 接入

代理通过 `CodexCli:Executable` 和 `CodexCli:SessionsPath` 配置 CLI 与会话目录。Windows 下默认使用 `codex.exe`，并从 `PATH` 解析为绝对路径；`/api/health` 的 `cliAvailable`、`cliPath` 与 `mappedWorkspaces` 可确认发送能力。代理从 GUI 会话元数据维护仅在电脑内存中的 thread→绝对工作区映射，手机不会获得绝对路径。手机回复使用 `codex exec --cd <workspace> --sandbox workspace-write --json resume <thread-id> <reply>` 恢复同一对话，参数直接传给进程而不经过 shell。若自动定位失败，可把 `Executable` 设置为 `where.exe codex` 输出的完整 `.exe` 路径。事件注入端点仍保留给 CLI 包装器与契约测试：

```powershell
$headers = @{ 'X-Device-Token' = '<配对后得到的令牌>' }
$body = @{ taskId='task-1'; project='课程项目'; title='实现功能'; type='Completed'; summary='当前对话已完成' } | ConvertTo-Json
Invoke-RestMethod http://localhost:5187/api/demo/events -Method Post -Headers $headers -ContentType application/json -Body $body
```

支持的事件类型：`Started`、`Progress`、`ApprovalRequested`、`WaitingReply`、`Completed`、`Failed`、`Resumed`、`Offline`。调用方传入稳定的 `id` 即可获得幂等去重。

## 安全边界

- 配对码随机生成、5 分钟过期、使用一次后立即失效。
- 设备令牌只保存 SHA-256 摘要；审批带请求 ID、有效期和一次性 nonce。
- 高风险批准必须二次确认；重复提交、过期请求和错误 nonce 均被拒绝。
- 事件写入前会脱敏常见密钥与 Windows 绝对路径。
- MVP 默认 HTTP 仅适合本机功能验证。校园网正式使用前，应在反向代理或代理本身启用 HTTPS，并在手机首次配对时核对显示的公钥指纹；不能把当前 HTTP 模式当作满足 PRD 端到端加密的生产实现。

## 验证

```powershell
dotnet build CodexPhoneReminder.sln
dotnet run --project tests/CodexPhoneReminder.Agent.Tests
```

## 已知 MVP 限制

- 解析器已读取本机 Codex Desktop 生成的结构化会话 JSONL；CLI 版本升级仍可能改变事件字段，在积累跨版本契约样本前不能宣称达到 95% 识别率。
- PWA 在 Android 后台的局域网通知能力受系统限制；需要后台提醒时应使用原生 APK。部分手机还需在系统设置中允许通知、前台服务和后台运行。
- 当前交付的是 debug APK，不是用于应用商店分发的 release APK/AAB。
- Reminder 审批会恢复同一 Codex CLI 对话，但不会绕过 CLI 自身审批和沙箱；新产生的系统级权限请求必须重新确认。
