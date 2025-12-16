using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using System;
using System.IO;
using System.Text;

// ========================================
// このスクリプトについて
// ========================================
// マイクで録音した音声を Whisper API で文字起こしし、
// ChatGPT API に送信して応答を得て、TTS API で音声化するスクリプトです。
//
// 【Cube の色の変化】
// - 白：待機状態
// - 赤：録音中
// - 青：API応答待ち（Whisper → ChatGPT）
// - 黄：TTS音声再生中
// - 白：再生完了後
// ========================================

public class CubeEvent2 : MonoBehaviour, IPointerClickHandler
{
    // ========================================
    // OpenAI API 設定
    // ========================================

    // OpenAI API Key（Whisper, ChatGPT, TTS で共通）
    string OpenAIAPIKey = "OpenAIAPIKey";

    // ChatGPT 設定
    string chatModel = "gpt-4o-mini";

    // TTS 設定
    string ttsModel = "tts-1";
    string ttsVoice = "alloy";  // alloy, echo, fable, onyx, nova, shimmer から選択可能

    // ========================================

    // Cube の Renderer（色変更用）
    Renderer cubeRenderer;

    // 音声再生用 AudioSource
    AudioSource audioSource;

    // マイクの開始・終了管理
    bool flagMicRecordStart = false;

    // マイクデバイスがキャッチできたかどうか
    bool catchedMicDevice = false;

    // 現在録音するマイクデバイス名
    string currentRecordingMicDeviceName = "null";

    // ========================================
    // マイクデバイスの設定
    // ========================================
    // Unity を実行して Console でマイクデバイス一覧を確認し、
    // 使いたいデバイスの [番号] を下記に設定してください
    // ========================================
    int recordingTargetMicDeviceID = 0;  // デフォルトは 0（最初のデバイス）

    // ヘッダーサイズ
    int HeaderByteSize = 44;

    // BitsPerSample
    int BitsPerSample = 16;

    // AudioFormat
    int AudioFormat = 1;

    // 録音する AudioClip
    AudioClip recordedAudioClip;

    // サンプリング周波数
    int samplingFrequency = 44100;

    // 最大録音時間[sec]
    int maxTimeSeconds = 10;

    // Wav データ
    byte[] dataWav;

    // Wisper API で受信した JSON データを Unity で扱うデータにする WhisperAPIResponseData ベースクラス
    [Serializable]
    public class WhisperAPIResponseData
    {
        public string text;
    }

    // ChatGPT API で受信した JSON データを Unity で扱うデータにする ResponseData ベースクラス
    // API仕様 : https://platform.openai.com/docs/api-reference/completions/object
    [Serializable]
    public class ResponseData
    {
        public string id;
        public string @object; // object は予約語なので @ を使ってエスケープしています
        public int created;
        public List<ResponseDataChoice> choices;
        public ResponseDataUsage usage;
    }

    [Serializable]
    public class ResponseDataUsage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }
    [Serializable]
    public class ResponseDataChoice
    {
        public int index;
        public RequestDataMessages message;
        public string finish_reason;
    }

    // ChatGPT API に送信する Unity データを JSON データ化する RequestData ベースクラス
    [Serializable]
    public class RequestData
    {
        public string model;
        public List<RequestDataMessages> messages;
    }

    [Serializable]
    public class RequestDataMessages
    {
        public string role;
        public string content;
    }

    // TTS API 用のリクエストデータクラス
    [Serializable]
    public class TTSRequestData
    {
        public string model;
        public string input;
        public string voice;
        public string response_format;
    }


    void Start()
    {
        catchedMicDevice = false;

        // Cube の Renderer を取得（色変更用）
        cubeRenderer = GetComponent<Renderer>();

        // AudioSource を取得（音声再生用）
        audioSource = GetComponent<AudioSource>();

        Launch();
    }

    void Launch()
    {
        // マイクデバイス一覧を表示
        Debug.Log("========================================");
        Debug.Log("マイクデバイス一覧");
        Debug.Log("========================================");

        for (int i = 0; i < Microphone.devices.Length; i++)
        {
            Debug.Log($"[{i}] {Microphone.devices[i]}");
        }

        Debug.Log("========================================");

        // ID でマイクデバイスを選択
        if (recordingTargetMicDeviceID >= 0 && recordingTargetMicDeviceID < Microphone.devices.Length)
        {
            currentRecordingMicDeviceName = Microphone.devices[recordingTargetMicDeviceID];
            catchedMicDevice = true;
            Debug.Log($"選択されたデバイス: [{recordingTargetMicDeviceID}] {currentRecordingMicDeviceName}");
            Debug.Log($"マイク捜索成功");
        }
        else
        {
            Debug.Log($"エラー: デバイスID {recordingTargetMicDeviceID} は範囲外です（0～{Microphone.devices.Length - 1} を指定してください）");
            Debug.Log($"マイク捜索失敗");
        }

    }

    void Update()
    {

    }

    // Cube の色を変更する関数
    void ChangeCubeColor(Color color)
    {
        if (cubeRenderer != null)
        {
            cubeRenderer.material.color = color;
        }
    }
    public void OnPointerClick(PointerEventData eventData)
    {
        if (catchedMicDevice)
        {
            if (flagMicRecordStart)
            {
                // Stop
                // マイクの録音を開始
                flagMicRecordStart = false;
                Debug.Log($"Mic Record Stop");

                RecordStop();

            }
            else
            {
                // Start
                // マイクの停止
                flagMicRecordStart = true;
                Debug.Log($"Mic Record Start");

                RecordStart();
            }
        }

    }

    void RecordStart()
    {
        // Cube の色を赤に変更（録音中）
        ChangeCubeColor(Color.red);

        // マイクの録音を開始して AudioClip を割り当て
        recordedAudioClip = Microphone.Start(currentRecordingMicDeviceName, false, maxTimeSeconds, samplingFrequency);
    }

    void RecordStop()
    {
        // マイクの停止
        Microphone.End(currentRecordingMicDeviceName);

        Debug.Log($"WAV データ作成開始");

        // using を使ってメモリ開放を自動で行う
        using (MemoryStream currentMemoryStream = new MemoryStream())
        {
            // ChunkID RIFF
            byte[] bufRIFF = Encoding.ASCII.GetBytes("RIFF");
            currentMemoryStream.Write(bufRIFF, 0, bufRIFF.Length);

            // ChunkSize
            byte[] bufChunkSize = BitConverter.GetBytes((UInt32)(HeaderByteSize + recordedAudioClip.samples * recordedAudioClip.channels * BitsPerSample / 8));
            currentMemoryStream.Write(bufChunkSize, 0, bufChunkSize.Length);

            // Format WAVE
            byte[] bufFormatWAVE = Encoding.ASCII.GetBytes("WAVE");
            currentMemoryStream.Write(bufFormatWAVE, 0, bufFormatWAVE.Length);

            // Subchunk1ID fmt
            byte[] bufSubchunk1ID = Encoding.ASCII.GetBytes("fmt ");
            currentMemoryStream.Write(bufSubchunk1ID, 0, bufSubchunk1ID.Length);

            // Subchunk1Size (16 for PCM)
            byte[] bufSubchunk1Size = BitConverter.GetBytes((UInt32)16);
            currentMemoryStream.Write(bufSubchunk1Size, 0, bufSubchunk1Size.Length);

            // AudioFormat (PCM=1)
            byte[] bufAudioFormat = BitConverter.GetBytes((UInt16)AudioFormat);
            currentMemoryStream.Write(bufAudioFormat, 0, bufAudioFormat.Length);

            // NumChannels
            byte[] bufNumChannels = BitConverter.GetBytes((UInt16)recordedAudioClip.channels);
            currentMemoryStream.Write(bufNumChannels, 0, bufNumChannels.Length);

            // SampleRate
            byte[] bufSampleRate = BitConverter.GetBytes((UInt32)recordedAudioClip.frequency);
            currentMemoryStream.Write(bufSampleRate, 0, bufSampleRate.Length);

            // ByteRate (=SampleRate * NumChannels * BitsPerSample/8)
            byte[] bufByteRate = BitConverter.GetBytes((UInt32)(recordedAudioClip.samples * recordedAudioClip.channels * BitsPerSample / 8));
            currentMemoryStream.Write(bufByteRate, 0, bufByteRate.Length);

            // BlockAlign (=NumChannels * BitsPerSample/8)
            byte[] bufBlockAlign = BitConverter.GetBytes((UInt16)(recordedAudioClip.channels * BitsPerSample / 8));
            currentMemoryStream.Write(bufBlockAlign, 0, bufBlockAlign.Length);

            // BitsPerSample
            byte[] bufBitsPerSample = BitConverter.GetBytes((UInt16)BitsPerSample);
            currentMemoryStream.Write(bufBitsPerSample, 0, bufBitsPerSample.Length);

            // Subchunk2ID data
            byte[] bufSubchunk2ID = Encoding.ASCII.GetBytes("data");
            currentMemoryStream.Write(bufSubchunk2ID, 0, bufSubchunk2ID.Length);

            // Subchuk2Size
            byte[] bufSubchuk2Size = BitConverter.GetBytes((UInt32)(recordedAudioClip.samples * recordedAudioClip.channels * BitsPerSample / 8));
            currentMemoryStream.Write(bufSubchuk2Size, 0, bufSubchuk2Size.Length);

            // Data
            float[] floatData = new float[recordedAudioClip.samples * recordedAudioClip.channels];
            recordedAudioClip.GetData(floatData, 0);

            foreach (float f in floatData)
            {
                byte[] bufData = BitConverter.GetBytes((short)(f * short.MaxValue));
                currentMemoryStream.Write(bufData, 0, bufData.Length);
            }

            Debug.Log($"WAV データ作成完了");

            dataWav = currentMemoryStream.ToArray();

            Debug.Log($"dataWav.Length {dataWav.Length}");

            // Cube の色を青に変更（API応答待ち）
            ChangeCubeColor(Color.blue);

            // まず Wisper API で文字起こし
            StartCoroutine(PostWhisperAPI());

        }

    }

    // Wisper API で文字起こし
    IEnumerator PostWhisperAPI()
    {
        // IMultipartFormSection で multipart/form-data のデータとして送れます
        // https://docs.unity3d.com/ja/2018.4/Manual/UnityWebRequest-SendingForm.html
        // https://docs.unity3d.com/ja/2019.4/ScriptReference/Networking.IMultipartFormSection.html
        // https://docs.unity3d.com/ja/2020.3/ScriptReference/Networking.MultipartFormDataSection.html
        List<IMultipartFormSection> formData = new List<IMultipartFormSection>();

        // https://platform.openai.com/docs/api-reference/audio/createTranscription
        // Whisper モデルを使う
        formData.Add(new MultipartFormDataSection("model", "whisper-1"));
        // 日本語で返答
        formData.Add(new MultipartFormDataSection("language", "ja"));
        // WAV データを入れる
        formData.Add(new MultipartFormFileSection("file", dataWav, "whisper01.wav", "multipart/form-data"));

        // HTTP リクエストする(POST メソッド) UnityWebRequest を呼び出し
        // 第 2 引数で上記のフォームデータを割り当てて multipart/form-data のデータとして送ります
        string urlWhisperAPI = "https://api.openai.com/v1/audio/transcriptions";
        UnityWebRequest request = UnityWebRequest.Post(urlWhisperAPI, formData);

        // OpenAI 認証は Authorization ヘッダーで Bearer のあとに API トークンを入れる
        request.SetRequestHeader("Authorization", $"Bearer {OpenAIAPIKey}");

        // ダウンロード（サーバ→Unity）のハンドラを作成
        request.downloadHandler = new DownloadHandlerBuffer();

        Debug.Log("WhisperAPI リクエスト開始");

        // リクエスト開始
        yield return request.SendWebRequest();


        // 結果によって分岐
        switch (request.result)
        {
            case UnityWebRequest.Result.InProgress:
                Debug.Log("WhisperAPI リクエスト中");
                break;

            case UnityWebRequest.Result.ProtocolError:
                Debug.Log("ProtocolError");
                Debug.Log(request.responseCode);
                Debug.Log(request.error);
                break;

            case UnityWebRequest.Result.ConnectionError:
                Debug.Log("ConnectionError");
                break;

            case UnityWebRequest.Result.Success:
                Debug.Log("WhisperAPI リクエスト成功");

                // コンソールに表示
                Debug.Log($"responseData: {request.downloadHandler.text}");

                WhisperAPIResponseData resultResponseWhisperAPI = JsonUtility.FromJson<WhisperAPIResponseData>(request.downloadHandler.text);

                // テキストが起こせたら Chat API に聞く
                StartCoroutine(PostChatAPI(resultResponseWhisperAPI.text));

                break;
        }

        // メモリリーク防止のため、UnityWebRequest を破棄
        request.Dispose();
    }

    // ChatGPT API
    IEnumerator PostChatAPI(string text)
    {
        // HTTP リクエストする(POST メソッド) UnityWebRequest を呼び出し
        UnityWebRequest request = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");

        RequestData requestData = new RequestData();
        // データを設定（設定された chatModel を使用）
        requestData.model = chatModel;
        RequestDataMessages currentMessage = new RequestDataMessages();
        // ロールは user
        currentMessage.role = "user";
        // 実際の質問
        currentMessage.content = text;
        List<RequestDataMessages> currentMessages = new List<RequestDataMessages>();
        currentMessages.Add(currentMessage);
        requestData.messages = currentMessages;
        Debug.Log($"currentMessages[0].content : {currentMessages[0].content}");

        // 送信データを JsonUtility.ToJson で JSON 文字列を作成
        // RequestData, RequestDataMessages の構造に基づいて変換してくれる
        string strJSON = JsonUtility.ToJson(requestData);
        Debug.Log($"strJSON : {strJSON}");
        // 送信データを Encoding.UTF8.GetBytes で byte データ化
        byte[] bodyRaw = Encoding.UTF8.GetBytes(strJSON);

        // アップロード（Unity→サーバ）のハンドラを作成
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        // ダウンロード（サーバ→Unity）のハンドラを作成
        request.downloadHandler = new DownloadHandlerBuffer();

        // JSON で送ると HTTP ヘッダーで宣言する
        request.SetRequestHeader("Content-Type", "application/json");
        // ChatGPT 用の認証を伝える設定
        request.SetRequestHeader("Authorization", $"Bearer {OpenAIAPIKey}");

        // リクエスト開始
        yield return request.SendWebRequest();

        Debug.Log("ChatGPT API リクエスト...");

        // 結果によって分岐
        switch (request.result)
        {
            case UnityWebRequest.Result.InProgress:
                Debug.Log("ChatGPT API リクエスト中");
                break;

            case UnityWebRequest.Result.ProtocolError:
                Debug.Log("ProtocolError");
                Debug.Log(request.responseCode);
                Debug.Log(request.error);
                // エラー時は白に戻す
                ChangeCubeColor(Color.white);
                break;

            case UnityWebRequest.Result.ConnectionError:
                Debug.Log("ConnectionError");
                // エラー時は白に戻す
                ChangeCubeColor(Color.white);
                break;

            case UnityWebRequest.Result.Success:
                Debug.Log("ChatGPT API リクエスト成功");

                // コンソールに表示
                Debug.Log($"responseData: {request.downloadHandler.text}");

                ResponseData resultResponse = JsonUtility.FromJson<ResponseData>(request.downloadHandler.text);

                // 返答
                string responseText = resultResponse.choices[0].message.content;
                Debug.Log($"返答: {responseText}");

                // TTS API を呼び出す（色は青のまま）
                StartCoroutine(PostTTSAPI(responseText));

                break;
        }

        // メモリリーク防止のため、UnityWebRequest を破棄
        request.Dispose();
    }

    // TTS API
    IEnumerator PostTTSAPI(string text)
    {
        // HTTP リクエストする(POST メソッド) UnityWebRequest を呼び出し
        UnityWebRequest request = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST");

        TTSRequestData requestData = new TTSRequestData();
        // データを設定
        requestData.model = ttsModel;
        requestData.input = text;
        requestData.voice = ttsVoice;
        requestData.response_format = "wav";

        string strJSON = JsonUtility.ToJson(requestData);
        Debug.Log($"TTS strJSON : {strJSON}");

        // 送信データを Encoding.UTF8.GetBytes で byte データ化
        byte[] bodyRaw = Encoding.UTF8.GetBytes(strJSON);

        // アップロード（Unity→サーバ）のハンドラを作成
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        // ダウンロード（サーバ→Unity）のハンドラを作成
        request.downloadHandler = new DownloadHandlerBuffer();

        // JSON で送ると HTTP ヘッダーで宣言する
        request.SetRequestHeader("Content-Type", "application/json");
        // TTS 用の認証を伝える設定
        request.SetRequestHeader("Authorization", $"Bearer {OpenAIAPIKey}");

        // リクエスト開始
        yield return request.SendWebRequest();

        Debug.Log("TTS API リクエスト...");

        // 結果によって分岐
        switch (request.result)
        {
            case UnityWebRequest.Result.InProgress:
                Debug.Log("TTS API リクエスト中");
                break;

            case UnityWebRequest.Result.ProtocolError:
                Debug.Log("ProtocolError");
                Debug.Log(request.responseCode);
                Debug.Log(request.error);
                // エラー時は白に戻す
                ChangeCubeColor(Color.white);
                break;

            case UnityWebRequest.Result.ConnectionError:
                Debug.Log("ConnectionError");
                // エラー時は白に戻す
                ChangeCubeColor(Color.white);
                break;

            case UnityWebRequest.Result.Success:
                Debug.Log("TTS API リクエスト成功");

                // コンソールに表示
                Debug.Log($"responseData Length: {request.downloadHandler.data.Length}");

                byte[] wavData = request.downloadHandler.data;

                // WAV データを AudioClip に変換
                AudioClip audioClip = WavToAudioClip(wavData, "tts_audio");
                audioSource.clip = audioClip;

                // 音声再生開始、Cube の色を黄色に変更
                audioSource.Play();
                ChangeCubeColor(Color.yellow);
                Debug.Log("音声再生開始");

                // 再生完了を待つ
                StartCoroutine(WaitForAudioEnd());

                break;
        }

        // メモリリーク防止のため、UnityWebRequest を破棄
        request.Dispose();
    }

    // 音声再生完了を待つ
    IEnumerator WaitForAudioEnd()
    {
        yield return new WaitWhile(() => audioSource.isPlaying);

        // 再生完了後、白に戻す
        ChangeCubeColor(Color.white);
        Debug.Log("音声再生完了");
    }

    // WAV バイトデータを AudioClip に変換
    AudioClip WavToAudioClip(byte[] fileBytes, string audioClipName)
    {
        using var memoryStream = new MemoryStream(fileBytes);

        // RIFF チェック
        var riffBytes = new byte[4];
        memoryStream.Read(riffBytes, 0, 4);
        if (Encoding.ASCII.GetString(riffBytes) != "RIFF")
            throw new ArgumentException("fileBytes is not the correct Wav file format.");

        // チャンクサイズをスキップ
        memoryStream.Seek(4, SeekOrigin.Current);

        // WAVE チェック
        var waveBytes = new byte[4];
        memoryStream.Read(waveBytes, 0, 4);
        if (Encoding.ASCII.GetString(waveBytes) != "WAVE")
            throw new ArgumentException("fileBytes is not the correct Wav file format.");

        // チャンクを動的に探索
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitPerSample = 0;
        bool fmtFound = false;
        int dataSize = 0;
        byte[] soundData = new byte[0];

        while (memoryStream.Position < memoryStream.Length)
        {
            // チャンクIDの読み取り
            var chunkIDBytes = new byte[4];
            memoryStream.Read(chunkIDBytes, 0, 4);
            var chunkID = System.Text.Encoding.ASCII.GetString(chunkIDBytes);

            // チャンクサイズの読み取り
            var chunkSizeBytes = new byte[4];
            memoryStream.Read(chunkSizeBytes, 0, 4);
            uint chunkSize = BitConverter.ToUInt32(chunkSizeBytes, 0);

            // チャンクサイズが 0xFFFFFFFF の場合、残りのデータを使用
            if (chunkSize == 0xFFFFFFFF)
            {
                chunkSize = (uint)(memoryStream.Length - memoryStream.Position);
            }

            // fmt チャンクの処理
            if (chunkID == "fmt ")
            {
                fmtFound = true;

                var fmtBytes = new byte[chunkSize];
                memoryStream.Read(fmtBytes, 0, (int)chunkSize);

                channels = BitConverter.ToUInt16(fmtBytes, 2);
                sampleRate = BitConverter.ToInt32(fmtBytes, 4);
                bitPerSample = BitConverter.ToUInt16(fmtBytes, 14);

                Debug.Log($"Channels: {channels}");
                Debug.Log($"Sample Rate: {sampleRate}");
                Debug.Log($"Bits Per Sample: {bitPerSample}");
            }

            // data チャンクの処理
            else if (chunkID == "data")
            {
                if (!fmtFound)
                    throw new InvalidOperationException("fmt chunk must appear before data chunk.");

                Debug.Log($"Data chunk found. Size: {chunkSize}");

                var data = new byte[chunkSize];
                memoryStream.Read(data, 0, (int)chunkSize);

                soundData = data;

                Debug.Log($"Successfully read {data.Length} bytes of audio data.");

                dataSize = data.Length;

                break;
            }
            else
            {
                // 不要なチャンクはスキップ
                memoryStream.Seek(chunkSize, SeekOrigin.Current);
            }
        }

        Debug.Log("WAV file parsing completed.");

        memoryStream.Dispose();

        return CreateAudioClip(soundData, channels, sampleRate, bitPerSample, audioClipName);
    }

    // AudioClip を作成
    AudioClip CreateAudioClip(byte[] data, int channels, int sampleRate, ushort bitPerSample, string audioClipName)
    {
        Debug.Log("CreateAudioClip");

        var audioClipData = bitPerSample switch
        {
            16 => Create16BITAudioClipData(data),
            32 => Create32BITAudioClipData(data),
            _ => throw new ArgumentException($"bitPerSample is not supported : bitPerSample = {bitPerSample}")
        };

        var audioClip = AudioClip.Create(audioClipName, audioClipData.Length, channels, sampleRate, false);
        audioClip.SetData(audioClipData, 0);
        return audioClip;
    }

    // 16bit WAV データを float 配列に変換
    float[] Create16BITAudioClipData(byte[] data)
    {
        var audioClipData = new float[data.Length / 2];
        var memoryStream = new MemoryStream(data);

        for (var i = 0; ; i++)
        {
            var target = new byte[2];
            var read = memoryStream.Read(target);

            if (read <= 0) break;

            audioClipData[i] = (float)BitConverter.ToInt16(target) / short.MaxValue;
        }

        return audioClipData;
    }

    // 32bit WAV データを float 配列に変換
    float[] Create32BITAudioClipData(byte[] data)
    {
        var audioClipData = new float[data.Length / 4];
        var memoryStream = new MemoryStream(data);

        for (var i = 0; ; i++)
        {
            var target = new byte[4];
            var read = memoryStream.Read(target);

            if (read <= 0) break;

            audioClipData[i] = (float)BitConverter.ToInt32(target) / int.MaxValue;
        }

        return audioClipData;
    }
}