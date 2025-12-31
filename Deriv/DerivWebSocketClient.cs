using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DerivSmartBotDesktop.Core;

namespace DerivSmartBotDesktop.Deriv
{
    public class DerivWebSocketClient : IDisposable, IProposalProvider
    {
        private readonly string _appId;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;

        private Task _receiveLoopTask;
        private Task _pingLoopTask;
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);

        private bool _authorized;
        private TaskCompletionSource<bool> _authorizeTcs;
        private int _nextReqId = 1;
        private readonly Dictionary<int, TaskCompletionSource<ProposalQuote>> _proposalRequests = new();
        private readonly object _proposalLock = new();

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
        public event Action<string, string, string> BuyError;
        // NEW: raised when the WebSocket is closed or the receive loop errors
        public event Action<string> ConnectionClosed;

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
            _pingLoopTask = Task.Run(PingLoopAsync);
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

        public async Task<ProposalQuote> GetProposalAsync(
            ProposalRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Symbol))
                throw new ArgumentException("Symbol is required.", nameof(request));

            if (string.IsNullOrWhiteSpace(request.ContractType))
                throw new ArgumentException("Contract type is required.", nameof(request));

            int reqId = Interlocked.Increment(ref _nextReqId);
            var tcs = new TaskCompletionSource<ProposalQuote>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_proposalLock)
            {
                _proposalRequests[reqId] = tcs;
            }

            using var _ = cancellationToken.Register(() =>
            {
                lock (_proposalLock)
                {
                    if (_proposalRequests.Remove(reqId, out var pending))
                    {
                        pending.TrySetCanceled(cancellationToken);
                    }
                }
            });

            var payload = new
            {
                proposal = 1,
                amount = request.Stake,
                basis = "stake",
                contract_type = request.ContractType,
                currency = request.Currency,
                duration = request.Duration,
                duration_unit = request.DurationUnit,
                symbol = request.Symbol,
                req_id = reqId
            };

            await SendAsync(payload);
            Log($"SEND: {JsonSerializer.Serialize(payload)}");

            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task BuyRiseFallAsync(
            string symbol,
            double stake,
            TradeSignal direction,
            string strategyName,
            Guid clientTradeId,
            int duration = 1,
            string durationUnit = "t",
            string currency = "USD")
        {
            // Map TradeSignal to "CALL"/"PUT"
            string contractType = direction == TradeSignal.Buy ? "CALL" : "PUT";
            if (duration <= 0) duration = 1;
            if (string.IsNullOrWhiteSpace(durationUnit)) durationUnit = "t";

            // Final price/amount to Deriv: decimal with 2 dp
            var price = Math.Round((decimal)stake, 2, MidpointRounding.AwayFromZero);

            var buyPayload = new
            {
                buy = 1,
                price = price,
                parameters = new
                {
                    amount = price,
                    basis = "stake",
                    contract_type = contractType,
                    currency = currency,
                    duration = duration,
                    duration_unit = durationUnit,
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

            await _sendSemaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }


        private async Task PingLoopAsync()
        {
            // Periodically send a lightweight ping to keep the WebSocket alive.
            if (_cts == null)
                return;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);

                    if (_ws == null || _ws.State != WebSocketState.Open)
                        continue;

                    var pingPayload = new { ping = 1 };
                    await SendAsync(pingPayload);
                    Log("SEND: {\"ping\":1}");
                }
            }
            catch (TaskCanceledException)
            {
                // Normal on shutdown
            }
            catch (Exception ex)
            {
                Log($"Ping loop error: {ex.Message}");
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
                            var reason = $"WebSocket closed by server: {result.CloseStatus} {result.CloseStatusDescription}";
                            Log(reason);
                            _authorized = false;
                            try
                            {
                                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "OK", CancellationToken.None);
                            }
                            catch
                            {
                                // ignore secondary close errors
                            }

                            // Notify listeners so they can attempt reconnect
                            ConnectionClosed?.Invoke(reason);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());

                    // Avoid spamming UI with every tick message; only log non-tick payloads.
                    if (!json.Contains("\"msg_type\":\"tick\""))
                    {
                        Log($"RECV: {json}");
                    }

                    ProcessMessage(json);
                }
                catch (OperationCanceledException)
                {
                    // normal on dispose/close
                }
                catch (Exception ex)
                {
                    var msg = $"Receive loop error: {ex.Message}";
                    Log(msg);
                    _authorized = false;
                    // Inform listeners (MainWindow) of unexpected disconnect
                    ConnectionClosed?.Invoke(msg);
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

                case "buy":
                    _ = HandleBuyAsync(root);
                    break;

                case "proposal_open_contract":
                    HandleContract(root);
                    break;

                case "proposal":
                    HandleProposal(root);
                    break;

                case "error":
                    HandleError(root);
                    break;
            }
        }

        private async Task HandleBuyAsync(JsonElement root)
        {
            // Handle response to a buy request so we can subscribe to the resulting contract.
            if (root.TryGetProperty("error", out var err))
            {
                string code = err.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
                string msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";

                string symbol = null;
                if (root.TryGetProperty("echo_req", out var echoReq) &&
                    echoReq.TryGetProperty("parameters", out var paramObj) &&
                    paramObj.TryGetProperty("symbol", out var symProp) &&
                    symProp.ValueKind == JsonValueKind.String)
                {
                    symbol = symProp.GetString();
                }

                Log($"Buy error [{code}] for symbol {symbol ?? "UNKNOWN"}: {msg}");

                // Notify listeners (SmartBotController) so they can react (e.g. disable symbol for session).
                BuyError?.Invoke(symbol, code, msg);

                return;
            }

            if (!root.TryGetProperty("buy", out var buyObj))
                return;

            long contractId = 0;
            if (buyObj.TryGetProperty("contract_id", out var cidProp) && cidProp.ValueKind == JsonValueKind.Number)
            {
                contractId = cidProp.GetInt64();
            }

            if (contractId == 0)
            {
                Log("Buy response missing contract_id; cannot subscribe to contract updates.");
                return;
            }

            string strategy = null;
            string clientTradeId = null;

            if (root.TryGetProperty("passthrough", out var pt))
            {
                if (pt.TryGetProperty("strategy", out var sProp) && sProp.ValueKind == JsonValueKind.String)
                    strategy = sProp.GetString();

                if (pt.TryGetProperty("client_trade_id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    clientTradeId = idProp.GetString();
            }

            var subPayload = new
            {
                proposal_open_contract = 1,
                contract_id = contractId,
                subscribe = 1,
                passthrough = new
                {
                    strategy = strategy,
                    client_trade_id = clientTradeId
                }
            };

            try
            {
                await SendAsync(subPayload);
                Log($"SEND: {JsonSerializer.Serialize(subPayload)}");
            }
            catch (Exception ex)
            {
                Log($"Error subscribing to contract {contractId}: {ex.Message}");
            }
        }

        private void HandleProposal(JsonElement root)
        {
            if (!root.TryGetProperty("req_id", out var reqIdProp) || !reqIdProp.TryGetInt32(out var reqId))
                return;

            TaskCompletionSource<ProposalQuote> tcs = null;
            lock (_proposalLock)
            {
                if (_proposalRequests.TryGetValue(reqId, out var pending))
                {
                    tcs = pending;
                    _proposalRequests.Remove(reqId);
                }
            }

            if (tcs == null)
                return;

            if (root.TryGetProperty("error", out var err))
            {
                string code = err.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
                string msg = err.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                tcs.TrySetException(new Exception($"Proposal error [{code}]: {msg}"));
                return;
            }

            if (!root.TryGetProperty("proposal", out var proposal))
            {
                tcs.TrySetException(new Exception("Proposal response missing payload."));
                return;
            }

            var quote = new ProposalQuote();

            if (proposal.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                quote.Id = idProp.GetString();

            if (proposal.TryGetProperty("payout", out var payoutProp) && payoutProp.TryGetDouble(out var payout))
                quote.Payout = payout;

            if (proposal.TryGetProperty("profit", out var profitProp) && profitProp.TryGetDouble(out var profit))
                quote.Profit = profit;

            if (proposal.TryGetProperty("ask_price", out var askProp) && askProp.TryGetDouble(out var askPrice))
                quote.AskPrice = askPrice;

            tcs.TrySetResult(quote);
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
            if (authObj.TryGetProperty("balance", out var balProp) &&
                TryGetDoubleFlexible(balProp, out double authBalance))
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

            if (balObj.TryGetProperty("balance", out var bProp) &&
                TryGetDoubleFlexible(bProp, out double balValue))
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

            // Passthrough for finished contracts is included on the root envelope,
            // not inside proposal_open_contract.
            if (root.TryGetProperty("passthrough", out var pt))
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

        private static bool TryGetDoubleFlexible(JsonElement element, out double value)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
                return true;

            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                return double.TryParse(text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            }

            value = 0.0;
            return false;
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

            try
            {
                _sendSemaphore?.Dispose();
            }
            catch { }
        }
    }
}
