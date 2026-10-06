# JevChatAnalyzer

微信 4.x 数据库直读的 Jev 聊天判读工具：从本机已登录微信的加密数据库直接取消息，交给可切换的判读引擎（云端服务 Jev 云端 / 本地 laya 进程内推理）做意图、情绪与风险判读。

## 特性

- **密钥读取**：`rwk.dll` 只读内存扫描（微信需登录运行），DPAPI 加密保存，支持手动输入兜底
- **数据库直读**：SQLCipher 4 纯 C# 页解密 → 临时明文副本（用完即删）→ `message_*.db` 分片聚合、Name2Id 发送者还原、群成员昵称
- **引用消息**：识别 `(57<<32)|49`，解析 refermsg XML，被引用原文并入判读上下文
- **判读双后端**：线协议 HTTP（云端 云端服务 / 本地 本地 HTTP 服务）+ NLaya 进程内（TorchSharp 直载 safetensors，按需下载）
- **语音转写**：VoiceInfo 定位 → silk 解码（24kHz）→ sherpa-onnx SenseVoiceSmall，模型按需下载
- **图片导出**：.dat V1/V2/V3 解密（XOR + AES-128-ECB）
- **隐私**：云端判读默认脱敏（昵称匿名化、号码/URL 掩蔽），历史记录 AES-GCM 加密落盘
- **候选回复**（可选，默认关）：判读小抄 → OpenAI 兼容端点 → 3 条候选

## 环境要求

- Windows 10 1809+（x64），.NET 10 Desktop Runtime（或使用自包含发布）
- 微信 4.1.x 已登录运行（密钥自动读取时）
- 本地 laya 判读：约 1.2GB 模型下载；语音转写：约 245MB 模型下载

## 构建

```powershell
dotnet build JevChatAnalyzer.sln -c Release -p:Platform=x64
```

安装包（MSIX）：见 `scripts/make-msix.ps1`。






