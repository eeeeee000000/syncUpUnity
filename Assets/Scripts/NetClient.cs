using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Alla meddelanden använder samma klass, fält som inte behövs är tomma
[Serializable]
public class Msg
{
    public string type;
    public string name;
    public string password;
    public string text;
    public string sender;
    public string partner;
    public string reason;
    public string error;
    public string mode;
    public int online;
    public string[] interests;
    public string[] common;     // gemensamma intressen
    public bool on;             // för typing
    public bool bot;
    public int id;              // meddelandets nummer, används för reaktioner
}

// WebSocket-klient. Tar emot i bakgrunden men alla events körs på Unitys huvudtråd.
// Funkar inte i WebGL.
public class NetClient : MonoBehaviour
{
    public event Action OnConnected;
    public event Action<string> OnDisconnected;
    public event Action<Msg> OnMessage;

    ClientWebSocket socket;
    CancellationTokenSource cts;
    readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
    readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    void Update()
    {
        while (mainThread.TryDequeue(out Action action)) action();
    }

    void OnDestroy()
    {
        Close();
    }

    // Körs på huvudtråden, om anslutningen inte stängts innan dess
    void Post(CancellationTokenSource owner, Action action)
    {
        mainThread.Enqueue(() =>
        {
            if (!owner.IsCancellationRequested) action();
        });
    }

    void PostDisconnected(CancellationTokenSource owner, string reason)
    {
        Post(owner, () => OnDisconnected?.Invoke(reason));
    }

    public async void Connect(string url)
    {
        Close();

        var mySocket = new ClientWebSocket();
        var myCts = new CancellationTokenSource();
        socket = mySocket;
        cts = myCts;

        try
        {
            await mySocket.ConnectAsync(new Uri(url), myCts.Token);
        }
        catch (Exception e)
        {
            PostDisconnected(myCts, "Kunde inte ansluta: " + e.Message);
            return;
        }

        Post(myCts, () => OnConnected?.Invoke());
        await ReceiveLoop(mySocket, myCts);
    }

    async Task ReceiveLoop(ClientWebSocket mySocket, CancellationTokenSource myCts)
    {
        var buffer = new byte[8192];

        try
        {
            while (mySocket.State == WebSocketState.Open)
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await mySocket.ReceiveAsync(new ArraySegment<byte>(buffer), myCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            PostDisconnected(myCts, "Servern stängde anslutningen.");
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    Post(myCts, () => Dispatch(json));
                }
            }
        }
        catch (Exception e)
        {
            PostDisconnected(myCts, "Anslutningen bröts: " + e.Message);
            return;
        }

        PostDisconnected(myCts, "Anslutningen bröts.");
    }

    void Dispatch(string json)
    {
        Msg msg;
        try
        {
            msg = JsonUtility.FromJson<Msg>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Trasigt meddelande från servern: " + e.Message);
            return;
        }

        if (msg != null && !string.IsNullOrEmpty(msg.type)) OnMessage?.Invoke(msg);
    }

    public async void Send(Msg msg)
    {
        var mySocket = socket;
        if (mySocket == null || mySocket.State != WebSocketState.Open) return;

        byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));

        await sendLock.WaitAsync();
        try
        {
            if (mySocket.State == WebSocketState.Open)
            {
                await mySocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Anslutningen håller på att stängas, ReceiveLoop tar hand om det
        }
        finally
        {
            sendLock.Release();
        }
    }

    // Stänger utan att OnDisconnected körs
    public void Close()
    {
        cts?.Cancel();
        if (socket != null)
        {
            try
            {
                socket.Abort();
                socket.Dispose();
            }
            catch (Exception)
            {
                // redan stängd
            }
        }
        socket = null;
        cts = null;
    }
}
