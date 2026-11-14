<p align="center">
  <img src="assets/logo.png" width="160" alt="JevChatAnalyzer" />
</p>

# JevChatAnalyzer

微信 4.x 数据库直读的 Jev 聊天判读工具：从本机已登录微信的加密数据库直接取消息，交给本机进程内的 laya 判读引擎（TorchSharp 直载权重，离线可用）做意图、情绪与风险判读；判读结论可写回微信数据库为一条引用消息，全程可撤销、可还原。

## 功能总览

| 模块 | 说明 |
|---|---|
| 密钥读取 | rwk.dll 只读内存扫描（微信需登录运行），DPAPI 加密保存，手动输入兜底 |
| 数据库直读 | 微信同源 sqlcipher.dll 直接开库（已在真实库验证），这是唯一的数据访问路径 |
| 消息解析 | message_*.db 多分片聚合、`Msg_<md5>` 分表、Name2Id 发送者还原、群昵称、`wxid:\n` 前缀剥离、zstd 压缩内容解压 |
| 引用消息 | `(57<<32)|49` 识别、refermsg XML 解析（被引用原文并入判读上下文） |
| 判读 | 七题口径（意图/紧急度/需要回复/风险/情绪/建议/回复风格）；唯一来源 = 进程内 laya（NLaya/TorchSharp 直载 safetensors）；模型未就绪时明确报错，不降级到任何远端 |
| 语音转写 | VoiceInfo 三级定位 → Silk 解码（微信同源 dll）→ SenseVoiceSmall int8（模型按需下载） |
| 判读台账 | 判读结论 + 写回状态 + 目标行原文快照，SQLCipher 加密落盘（密钥经 DPAPI 保护） |
| 候选回复 | 可选：判读小抄 → OpenAI 兼容端点 → 3 条候选（默认关）；可复制，或经「前台窗口属于微信」校验后粘贴进微信输入框（不发送） |
| 写回 | 判读结论自动写进对方最新消息的引用块（正文不变）；目标消息本身是对方的引用回复时改为新增一条带 `jca_annotation` 标记的气泡，不改写原行。先记账再单事务改写，原文快照入台账，可逐行撤销与还原（不复制整库） |

> **界面只有「对话与监听 / 判读历史 / 设置」三页，不提供微信消息展示界面**：主页面「对话与监听」同时承担会话列表（头像、群聊/单聊筛选、逐会话监听开关、分析/记录）与判读结果卡片；任何界面都不展示消息原文。

## 快速开始

1. **构建**：`powershell -File scripts\build.ps1 -Configuration Release`（会先停掉运行中的实例，逐项目构建 Core/App 后跑测试；`-SkipTests` 可跳过），运行 `src\JevChatAnalyzer.App\bin\x64\Release\...\JevChatAnalyzer.exe`（免安装）
   - 清单已声明 `requireAdministrator`：读取微信进程内存（rwk.dll）需要与微信同级权限。UAC 开启时启动会请求一次提权；微信若以普通权限运行，也可在设置页点「以管理员身份重启」按需提权
2. **读密钥**：设置 → 微信密钥 → 自动读取（微信需已登录）；失败原因与原始返回记录在 `%LOCALAPPDATA%\JevChatAnalyzer\keyread.log`
3. **判读**：「对话与监听」页 → 打开微信数据 → 选择会话 → 点「分析」（自动取该会话对方最新一条可分析消息，语音先转写）
4. **判读引擎**（必需）：设置 → 本地 laya 判读 → 下载模型（约 700MB，之后离线判读）
5. **语音转写**（可选）：设置 → 语音转写 → 下载 SenseVoice（约 245MB）
6. **安装包**：`powershell -File scripts\make-msix.ps1` 产出签名 MSIX

## 数据安全边界

- 原始微信数据（进程内存、`xwechat_files`）**一律只读**；唯一例外是写回：先记账再改写单行、原文快照入台账、可逐行还原到本应用修改之前，不保留整库备份
- 密钥与判读台账落在 `%LOCALAPPDATA%\JevChatAnalyzer`：密钥经 DPAPI 保护，台账整库 SQLCipher 加密
- 判读全程在本机进程内完成（laya），消息与判读结论都不出机；因此不需要脱敏管线

## 环境要求

- Windows 10 1809+（x64）；开发构建需 .NET 10 SDK
- 密钥自动读取：微信 4.1.x 已登录运行
- 判读：本地 laya 模型（设置页按需下载，约 700MB）
