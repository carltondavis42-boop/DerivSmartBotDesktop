using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DerivSmartBotDesktop.Core;

namespace DerivSmartBotDesktop.Deriv
{
    public class DerivWebSocketClient : IDisposable
    {
        private readonly string _appId;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;

        private Task _receiveLoopTask;
        private readonly object _sendLock = new();

        private bool _authorized;
        private TaskCompletionSource<bool> _authorizeTcs;

        public bool IsConnected => _ws?.State == WebSocketState.Open;
        public bool IsAuthorized => _authorized;

        private double _balance;
        public double Balance => _balance;

        public string LoginId { get; private set; }
        public string Currency { get; private set; }

        public event Action<string> LogMessage;
        public event Action<Tick> TickReceived;
        public event Action<double> BalanceUpdated;
        public event Action<string, Guid, double> ContractFinished;

        public DerivWebSocketClient(string appId)
        {
            _appId = appId ?? throw new ArgumentNullException(nameof(appId));
        }

        public async Task ConnectAsync()
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
                return;

            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            var uri = new Uri($"wss://ws.derivws.com/websockets/v3?app_id={_appId}");
            Log($"Connecting to {uri}...");
            await _ws.ConnectAsync(uri, _cts.Token);
            Log("WebSocket connected.");

            _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        }

        public async Task AuthorizeAsync(string apiToken)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("API token is required.", nameof(apiToken));

            _authorizeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var payload = new
            {
                authorize = apiToken.Trim()
            };
            await SendAsync(payload);
        }

        public Task WaitUntilAuthorizedAsync()
        {
            return _authorizeTcs?.Task ?? Task.CompletedTask;
        }

        public async Task SubscribeTicksAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol is required.", nameof(symbol));

            var payload = new
            {
                ticks = symbol,
                subscribe = 1
            };
            await SendAsync(payload);
            Log($"SEND: {JsonSerializer.Serialize(payload)}");
        }

        public async Task RequestBalanceAsync()
        {
            var payload = new
            {
                balance = 1,
                subscribe = 1
            };
            await SendAsync(payload);
            Log($"SEND: {JsonSerializer.Serialize(payload)}");
        }

        public async Task BuyRiseFallAsync(
            string symbol,
            double stake,
            TradeSignal direction,
            string strategyName,
            Guid clientTradeId,
            int durationTicks = 1,
            string currency = "USD")
        {
            // Map TradeSignal to "CALL"/"PUT"
            string contractType = direction == TradeSignal.Buy ? "CALL" : "PUT";

            var buyPayload = new
            {
                buy = 1,
                price = stake,
                parameters = new
                {
                    amount = stake,
                    basis = "stake",
                    contract_type = contractType,
                    currency = currency,
                    duration = durationTicks,
                    duration_unit = "t",
                    symbol = symbol
                },
                passthrough = new
                {
                    strategy = strategyName,
                    client_trade_id = clientTradeId.ToString()
                }
            };

            await SendAsync(buyPayload);
            Log($"SEND: {JsonSerializer.Serialize(buyPayload)}");
        }

        private async Task SendAsync(object payload)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            string json = JsonSerializer.Serialize(payload);
            var buffer = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(buffer);

            lock (_sendLock)
            {
                // Serialize sends to avoid interleaving frames
                _ws.SendAsync(segment, WebSocketMessageType.Text, true, _cts.Token).Wait();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];

            while (_ws != null && _ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                try
                {
                    var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Log($"WebSocket closed by server: {result.CloseStatus} {result.CloseStatusDescription}");
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "OK", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    Log($"RECV: {json}");

                    ProcessMessage(json);
                }
                catch (OperationCanceledException)
                {
                    // normal on dispose/close
                }
                catch (Exception ex)
                {
                    Log($"Receive loop error: {ex.Message}");
                    // If something fatal happens, break and let the app decide what to do
                    break;
                }
            }
        }

        private void ProcessMessage(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("msg_type", out var msgTypeProp))
                return;

            string msgType = msgTypeProp.GetString();

            switch (msgType)
            {
                case "authorize":
                    HandleAuthorize(root);
                    break;

                case "tick":
                    HandleTick(root);
                    break;

                case "balance":
                    HandleBalance(root);
                    break;

                case "proposal_open_contract":
                    HandleContract(root);
                    break;

                case "error":
                    HandleError(root);
                    break;
            }
        }

        private void HandleAuthorize(JsonElement root)
        {
            if (root.TryGetProperty("error", out var err))
            {
                string code = err.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
                string msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                Log($"Authorize error [{code}]: {msg}");
                _authorizeTcs?.TrySetException(new Exception(msg));
                return;
            }

            if (!root.TryGetProperty("authorize", out var authObj))
                return;

            LoginId = authObj.TryGetProperty("loginid", out var loginProp) ? loginProp.GetString() : null;
            Currency = authObj.TryGetProperty("currency", out var curProp) ? curProp.GetString() : null;

            // Deriv sometimes includes "balance" directly in authorize (virtual account, etc.)
            if (authObj.TryGetProperty("balance", out var balProp) && balProp.TryGetDouble(out double authBalance))
            {
                _balance = authBalance;
                BalanceUpdated?.Invoke(_balance);
                Log($"Balance update (from authorize): {_balance}");
            }

            _authorized = true;
            _authorizeTcs?.TrySetResult(true);

            Log($"Authorized: loginid={LoginId}, currency={Currency}");
        }

        private void HandleTick(JsonElement root)
        {
            if (!root.TryGetProperty("tick", out var tickObj))
                return;

            string symbol = tickObj.TryGetProperty("symbol", out var symProp) ? symProp.GetString() : null;
            double quote = tickObj.TryGetProperty("quote", out var qProp) ? qProp.GetDouble() : 0.0;
            long epoch = tickObj.TryGetProperty("epoch", out var eProp) ? eProp.GetInt64() : 0;

            var tick = new Tick
            {
                Symbol = symbol,
                Quote = quote,
                Time = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
            };

            TickReceived?.Invoke(tick);
        }

        private void HandleBalance(JsonElement root)
        {
            if (root.TryGetProperty("error", out var err))
            {
                string code = err.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
                string msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                Log($"Deriv error [{code}]: {msg}");
                return;
            }

            if (!root.TryGetProperty("balance", out var balObj))
                return;

            if (balObj.TryGetProperty("balance", out var bProp) && bProp.TryGetDouble(out double balValue))
            {
                _balance = balValue;
                BalanceUpdated?.Invoke(_balance);
                Log($"Balance update: {_balance}");
            }
        }

        private void HandleContract(JsonElement root)
        {
            // Finished contract P/L reporting
            if (!root.TryGetProperty("proposal_open_contract", out var poc))
                return;

            // We expect "is_sold": 1 for finished contracts
            if (!poc.TryGetProperty("is_sold", out var soldProp) || soldProp.GetInt32() != 1)
                return;

            double profit = 0.0;
            if (poc.TryGetProperty("profit", out var profProp) && profProp.ValueKind == JsonValueKind.String)
            {
                double.TryParse(profProp.GetString(), out profit);
            }
            else if (poc.TryGetProperty("profit", out var profNum) && profNum.ValueKind == JsonValueKind.Number)
            {
                profit = profNum.GetDouble();
            }

            string strategy = null;
            string clientTradeIdRaw = null;

            if (poc.TryGetProperty("passthrough", out var pt))
            {
                if (pt.TryGetProperty("strategy", out var sProp) && sProp.ValueKind == JsonValueKind.String)
                    strategy = sProp.GetString();

                if (pt.TryGetProperty("client_trade_id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    clientTradeIdRaw = idProp.GetString();
            }

            if (!Guid.TryParse(clientTradeIdRaw, out Guid clientTradeId))
            {
                // If no guid, still log
                Log($"Contract finished (no client GUID). Strategy={strategy}, Profit={profit:F2}");
                return;
            }

            ContractFinished?.Invoke(strategy, clientTradeId, profit);
        }

        private void HandleError(JsonElement root)
        {
            if (!root.TryGetProperty("error", out var err))
                return;

            string code = err.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
            string msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";

            Log($"Deriv error [{code}]: {msg}");

            if (code == "AuthorizationRequired")
            {
                _authorized = false;
            }
        }

        private void Log(string msg)
        {
            LogMessage?.Invoke(msg);
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { }

            try
            {
                _ws?.Dispose();
            }
            catch { }
        }
    }
}
