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
// Chat API（OpenAI または Ollama）に送信して応答を得るスクリプトです。
//
// 【Cube の色の変化】
// - 白：待機状態
// - 赤：録音中
// - 青：API応答待ち
// - 白：応答受信後
// ========================================

public class CubeEvent : MonoBehaviour, IPointerClickHandler
{
    // ========================================
    // Chat API 設定の切り替え方法
    // ========================================
    // 1. OpenAI を使う場合：
    //    下の「OpenAI ChatGPT を使う場合」のブロックをそのままにする
    //    「Ollama を使う場合」のブロックをコメントアウトしたままにする
    //
    // 2. Ollama を使う場合：
    //    「OpenAI ChatGPT を使う場合」のブロックをコメントアウトする
    //    「Ollama を使う場合」のブロックのコメントを外す
    //    ※Ollama がローカルで起動している必要があります

    // OpenAI ChatGPT を使う場合
    string chatAPIType = "OpenAI";
    string chatModel = "gpt-4o-mini";
    string chatAPIURL = "https://api.openai.com/v1/chat/completions";
    bool useAPIKey = true;

    // Ollama を使う場合（ローカル）
    // string chatAPIType = "Ollama";
    // string chatModel = "granite3.2:2b";
    // string chatAPIURL = "http://localhost:11434/v1/chat/completions";
    // bool useAPIKey = false;

    // ========================================

    // Cube の Renderer（色変更用）
    Renderer cubeRenderer;

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

    // OpenAIAPIKey
    // WhisperAPI と ChatGPTAPI で共通
    string OpenAIAPIKey = "OpenAIAPIKey";

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


    void Start()
    {
        catchedMicDevice = false;

        // Cube の Renderer を取得（色変更用）
        cubeRenderer = GetComponent<Renderer>();

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

    // Chat API（OpenAI または Ollama）
    IEnumerator PostChatAPI(string text)
    {
        // HTTP リクエストする(POST メソッド) UnityWebRequest を呼び出し
        // 設定された chatAPIURL を使用
        UnityWebRequest request = new UnityWebRequest(chatAPIURL, "POST");

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
        // API キーが必要な場合のみ認証ヘッダーを設定（OpenAI の場合）
        if (useAPIKey)
        {
            request.SetRequestHeader("Authorization", $"Bearer {OpenAIAPIKey}");
        }

        // リクエスト開始
        yield return request.SendWebRequest();

        Debug.Log($"{chatAPIType} Chat API リクエスト...");

        // 結果によって分岐
        switch (request.result)
        {
            case UnityWebRequest.Result.InProgress:
                Debug.Log($"{chatAPIType} Chat API リクエスト中");
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
                Debug.Log($"{chatAPIType} Chat API リクエスト成功");

                // コンソールに表示
                Debug.Log($"responseData: {request.downloadHandler.text}");

                ResponseData resultResponse = JsonUtility.FromJson<ResponseData>(request.downloadHandler.text);

                // 返答
                Debug.Log($"resultResponse.choices[0].message : {resultResponse.choices[0].message.content}");

                // 応答受信後、Cube の色を白に戻す
                ChangeCubeColor(Color.white);

                break;
        }

        // メモリリーク防止のため、UnityWebRequest を破棄
        request.Dispose();
    }
}