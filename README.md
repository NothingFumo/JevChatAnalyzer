# JevChatAnalyzer

微信 4.x 数据库直读的 Jev 聊天判读工具：从本机已登录微信的加密数据库直接取消息，交给可切换的判读引擎（云端服务 Jev 云端 / 本地 HTTP 服务 / laya 进程内）做意图、情绪与风险判读；判读结论可写回微信数据库为一条引用消息，全程可撤销、可还原。

## 功能总览

| 模块 | 说明 |
|---|---|
| 密钥读取 | rwk.dll 只读内存扫描（微信需登录运行），DPAPI 加密保存，手动输入兜底 |
| 数据库直读 | 微信同源 sqlcipher.dll 直接开库（已在真实库验证）；纯 C# SQLCipher 4 页解密作为导出/容灾回退 |
| 消息解析 | message_*.db 多分片聚合、`Msg_<md5>` 分表、Name2Id 发送者还原、群昵称、`wxid:\n` 前缀剥离、zstd 压缩内容解压 |
| 引用消息 | `(57<<32)|49` 识别、refermsg XML 解析（被引用原文并入判读上下文） |
| 判读 | 七题口径（意图/紧急度/需要回复/风险/情绪/建议/回复风格）；线协议三后端：云端服务 云端、本地 HTTP 服务、NLaya 进程内（TorchSharp 直载 safetensors）；熔断与自动降级 |
| 语音转写 | VoiceInfo 三级定位 → Silk 解码（微信同源 dll）→ SenseVoiceSmall int8（模型按需下载） |
| 图片导出 | .dat V1/V2/V3 解密（XOR + AES-128-ECB） |
| 判读历史 | AES-256-GCM 加密落盘，密钥 DPAPI 保护 |
| 候选回复 | 可选：判读小抄 → OpenAI 兼容端点 → 3 条候选（默认关） |
| 写回 | 判读结论写回为引用消息；自动整库备份（SHA-256 记账）→ 按行撤销 → 整库还原 |

## 快速开始

1. **构建**：`powershell -File scripts\build.ps1 -Configuration Release`，运行 `src\JevChatAnalyzer.App\bin\x64\Release\...\JevChatAnalyzer.exe`（免安装）
2. **读密钥**：设置 → 微信密钥 → 自动读取（微信需已登录）
3. **判读**：会话判读页 → 打开微信数据 → 选会话 → 选消息 → 判读
4. **本地引擎**（可选）：设置 → 本地引擎模型 → 下载（约 700MB，之后离线判读）
5. **语音转写**（可选）：设置 → 语音转写 → 下载 SenseVoice（约 245MB）
6. **安装包**：`powershell -File scripts\make-msix.ps1` 产出签名 MSIX

## 数据安全边界

- 原始微信数据（进程内存、`xwechat_files`）**一律只读**；唯一例外是写回功能：显式确认 + 微信退出检查 + 写前整库备份 + 可撤销/可还原
- 密钥、历史、备份全部落在 `%LOCALAPPDATA%\JevChatAnalyzer`，敏感字段 DPAPI/AES-GCM 加密
- 云端判读默认脱敏（说话人匿名化、号码/URL/证件号掩蔽）；本地后端不经过脱敏管线（数据不出机）








## 环境要求

- Windows 10 1809+（x64）；开发构建需 .NET 10 SDK
- 密钥自动读取：微信 4.1.x 已登录运行
- 判读：云端服务 API key（环境变量 `CLOUD_API_KEY`）或本地 laya 模型
