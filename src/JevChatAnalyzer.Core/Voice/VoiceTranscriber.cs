using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JevChatAnalyzer.Core.Net;
using SherpaOnnx;

namespace JevChatAnalyzer.Core.Voice;

/// <summary>SenseVoice 转写模型文件组。</summary>
public sealed record VoiceModelFiles(string ModelPath, string TokensPath)
{
    public bool Exists => File.Exists(ModelPath) && File.Exists(TokensPath);
}

/// <summary>模型下载进度。</summary>
public sealed record DownloadProgress(string FileName, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? BytesReceived * 100.0 / TotalBytes : null;
}

/// <summary>
/// 语音转写：sherpa-onnx SenseVoiceSmall（int8），模型按需下载（modelscope 优先，HF 兜底），
/// 本地化路线；转写结果按 server_id 缓存。
/// </summary>
public sealed class VoiceTranscriber : IDisposable
{
    public const string DefaultModelDirName = "sensevoice";
    public const long ModelDownloadBytes = 245 * 1024 * 1024; // 约 245MB（提示用）

    /// <summary>modelscope 直链（国内快）；hf / hf-mirror 依次兜底（匿名 401/403 时换源）。</summary>
    public static readonly (string Name, string Url)[] ModelSources =
    [
        ("model.int8.onnx", "https://modelscope.cn/models/pengzhendong/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/master/model.int8.onnx"),
        ("tokens.txt", "https://modelscope.cn/models/pengzhendong/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/master/tokens.txt"),
        ("hf-model.int8.onnx", "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/main/model.int8.onnx"),
        ("hf-tokens.txt", "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/main/tokens.txt"),
        ("hfm-model.int8.onnx", "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/main/model.int8.onnx"),
        ("hfm-tokens.txt", "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue/resolve/main/tokens.txt"),
    ];

    private OfflineRecognizer? _recognizer;
    private readonly string _modelDir;
    private readonly string _cachePath;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = [];

    public VoiceTranscriber(string? modelDir = null, string? cachePath = null)
    {
        _modelDir = modelDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", DefaultModelDirName);
        _cachePath = cachePath ?? Path.Combine(_modelDir, "transcripts.json");
        LoadCache();
    }

    public VoiceModelFiles ModelFiles => new(
        Path.Combine(_modelDir, "model.int8.onnx"),
        Path.Combine(_modelDir, "tokens.txt"));

    /// <summary>模型是否已就绪。</summary>
    public bool IsModelReady => ModelFiles.Exists;

    /// <summary>
    /// 下载模型（已就绪时直接返回）。文件按序下载到临时名，成功后改名；
    /// 取消令牌可中断。progress 回调线程池线程上触发。
    /// </summary>
    public async Task DownloadModelAsync(CancellationToken ct = default, IProgress<DownloadProgress>? progress = null)
    {
        Directory.CreateDirectory(_modelDir);
        // 每个目标文件按源顺序尝试：modelscope → huggingface → hf-mirror
        var files = new (string Target, string[] Sources)[]
        {
            ("model.int8.onnx", [ModelSources[0].Url, ModelSources[2].Url, ModelSources[4].Url]),
            ("tokens.txt", [ModelSources[1].Url, ModelSources[3].Url, ModelSources[5].Url]),
        };
        foreach (var (target, sources) in files)
        {
            var dest = Path.Combine(_modelDir, target);
            if (File.Exists(dest) && new FileInfo(dest).Length > 1024)
            {
                continue;
            }
            var errors = new List<string>();
            foreach (var source in sources)
            {
                try
                {
                    await HttpDownload.DownloadAsync(source, dest, ct, progress);
                    errors.Clear();
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    errors.Add($"{new Uri(source).Host}: {e.Message}"); // 401/403/网络失败 → 换下一源
                }
            }
            if (errors.Count == sources.Length)
            {
                throw new InvalidOperationException(
                    $"下载 {target} 失败（已尝试 modelscope、huggingface、hf-mirror）：\n" +
                    string.Join("\n", errors));
            }
        }
    }

    /// <summary>转写一条语音（silk BLOB）。缓存命中直接返回。</summary>
    public string Transcribe(byte[] silkData, string cacheKey)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }
        var audio = SilkDecoder.DecodeToPcm(silkData);
        var text = TranscribePcm(audio.Pcm, audio.SampleRate);
        lock (_lock)
        {
            _cache[cacheKey] = text;
            SaveCache();
        }
        return text;
    }

    /// <summary>直接转写 PCM（自动重采样到 16k）。</summary>
    public string TranscribePcm(short[] pcm, int sampleRate)
    {
        if (!IsModelReady)
        {
            throw new VoiceException("SenseVoice 模型未就绪。请先在设置中下载模型。");
        }
        var recognizer = EnsureRecognizer();
        var samples = SilkDecoder.ToFloat(SilkDecoder.Resample(pcm, sampleRate, 16000));
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        recognizer.Decode(stream);
        var text = stream.Result.Text?.Trim() ?? "";
        // SenseVoice 输出可能带情感/事件标签（[HAPPY] 等），剥离方括号标签
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[[A-Z_a-z]+\]", "").Trim();
    }

    private OfflineRecognizer EnsureRecognizer()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }
        var files = ModelFiles;
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.SenseVoice.Model = files.ModelPath;
        config.ModelConfig.SenseVoice.Language = "zh";
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = files.TokensPath;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        _recognizer = new OfflineRecognizer(config);
        return _recognizer;
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cachePath)) ?? [];
            }
        }
        catch (JsonException)
        {
            _cache = [];
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(_modelDir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (IOException)
        {
            // 缓存写失败不影响转写结果
        }
    }

    /// <summary>转写缓存键：server_id 或 create_time+local_id 兜底。</summary>
    public static string CacheKey(long? serverId, long createTime, long localId) =>
        serverId is { } sid ? $"s{sid}" : $"t{createTime}_l{localId}";

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
