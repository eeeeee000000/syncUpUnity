using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Ett meddelande som skickas till eller tas emot från servern (som JSON).
/// Alla meddelandetyper delar samma fält, de som inte används lämnas tomma.
/// Hela protokollet beskrivs i server/hub.js.
/// </summary>
[Serializable]
public class Msg
{
    public string type;         // anon, register, login, find, next, bot, chat, typing, icebreaker, react, report, ...
    public string name;         // användarnamn (register/login) eller ditt namn (welcome/auth_ok)
    public string password;
    public string text;         // chattext, infotext, isbrytarfråga eller meddelande från admin
    public string sender;       // vem som skrev ett chattmeddelande eller bad om en isbrytare
    public string partner;      // vem du blev matchad med
    public string reason;       // orsak vid rapport
    public string error;        // felmeddelande vid inloggning
    public string mode;         // anon eller account
    public int online;          // antal anslutna just nu
    public string[] interests;  // dina intressen (find)
    public string[] common;     // intressen du har gemensamt med din partner (matched)
    public bool on;             // skriver/skriver inte (typing)
    public bool bot;            // partnern är syncBot (matched)
    public int id;              // meddelandets nummer i samtalet (chat), eller vilket meddelande en reaktion gäller
}

/// <summary>
/// Tunn WebSocket-klient. Nätverkskoden körs i bakgrunden men alla events
/// (OnConnected, OnDisconnected, OnMessage) anropas på Unitys huvudtråd.
/// Fungerar i editorn och i Windows/Mac/Linux/Android/iOS-byggen, inte i WebGL.
/// </summary>
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

    /// <summary>Lägger en åtgärd i kö till huvudtråden, men bara om anslutningen inte stängts under tiden.</summary>
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
            Debug.LogWarning("Ignorerade ett trasigt meddelande från servern: " + e.Message);
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
            // Anslutningen är på väg ner. Mottagningsloopen märker det och rapporterar via OnDisconnected.
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>Stänger anslutningen utan att OnDisconnected anropas.</summary>
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
                // Socketen var redan stängd, inget mer att göra.
            }
        }
        socket = null;
        cts = null;
    }
}
