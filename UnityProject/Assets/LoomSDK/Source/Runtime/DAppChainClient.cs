﻿using Loom.Google.Protobuf;
using Loom.Chaos.NaCl;
using UnityEngine;
using System.Threading.Tasks;
using System;
using Loom.Newtonsoft.Json;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Loom.Client.Internal;
using Loom.Client.Protobuf;

#if UNITY_WEBGL && !UNITY_EDITOR
using Loom.Client.Unity.Internal.UnityAsyncAwaitUtil;
#endif

namespace Loom.Client
{
    /// <summary>
    /// Writes to & reads from a Loom DAppChain.
    /// </summary>
    public class DAppChainClient : IDisposable
    {
        private const string LogTag = "Loom.DAppChainClient";

        private readonly Dictionary<EventHandler<RawChainEventArgs>, EventHandler<JsonRpcEventData>> eventSubs =
            new Dictionary<EventHandler<RawChainEventArgs>, EventHandler<JsonRpcEventData>>();

        private IRpcClient writeClient;
        private IRpcClient readClient;
        private ILogger logger = NullLogger.Instance;

        /// <summary>
        /// RPC client to use for submitting transactions.
        /// </summary>
        public IRpcClient WriteClient => this.writeClient;

        /// <summary>
        /// RPC client to use for querying DAppChain state.
        /// </summary>
        public IRpcClient ReadClient => this.readClient;
        
        /// <summary>
        /// Middleware to apply when committing transactions.
        /// </summary>
        public TxMiddleware TxMiddleware { get; set; }

        /// <summary>
        /// Whether clients will attempt to connect automatically when in Disconnected state
        /// before communicating.
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// Logger to be used for logging, defaults to <see cref="NullLogger"/>.
        /// </summary>
        public ILogger Logger {
            get {
                return this.logger;
            }
            set {
                if (value == null)
                {
                    value = NullLogger.Instance;
                }

                this.logger = value;
            }
        }

        /// <summary>
        /// Maximum number of times a tx should be resent after being rejected because of a bad nonce.
        /// Defaults to 5.
        /// </summary>
        public int NonceRetries { get; set; } = 5;

        /// <summary>
        /// Events emitted by the DAppChain.
        /// </summary>
        public event EventHandler<RawChainEventArgs> ChainEventReceived
        {
            add
            {
                this.SubReadClient(value);
            }
            remove
            {
                this.UnsubReadClient(value);
            }
        }

        /// <summary>
        /// Constructs a client to read & write data from/to a Loom DAppChain.
        /// </summary>
        /// <param name="writeClient">RPC client to use for submitting transactions.</param>
        /// <param name="readClient">RPC client to use for querying DAppChain state.</param>
        public DAppChainClient(IRpcClient writeClient, IRpcClient readClient)
        {
            this.writeClient = writeClient;
            this.readClient = readClient;
        }

        public void Dispose()
        {
            if (this.writeClient != null)
            {
                this.writeClient.Dispose();
                this.writeClient = null;
            }
            if (this.readClient != null)
            {
                this.readClient.Dispose();
                this.readClient = null;
            }
        }

        /// <summary>
        /// Gets a nonce for the given public key.
        /// </summary>
        /// <param name="key">A hex encoded public key, e.g. 441B9DCC47A734695A508EDF174F7AAF76DD7209DEA2D51D3582DA77CE2756BE</param>
        /// <returns>The nonce.</returns>
        public async Task<ulong> GetNonceAsync(string key)
        {
            if (this.readClient == null)
                throw new InvalidOperationException("Read client is not set");

            await EnsureConnected();
            string nonce = await this.readClient.SendAsync<string, NonceParams>(
                "nonce", new NonceParams { Key = key }
            );
            return UInt64.Parse(nonce); 
        }

        /// <summary>
        /// Tries to resolve a contract name to an address.
        /// </summary>
        /// <param name="contractName">Name of a smart contract on a Loom DAppChain.</param>
        /// <exception cref="Exception">If a contract matching the given name wasn't found</exception>
        public async Task<Address> ResolveContractAddressAsync(string contractName)
        {
            if (this.readClient == null)
                throw new InvalidOperationException("Read client is not set");

            await EnsureConnected();
            var addrStr = await this.readClient.SendAsync<string, ResolveParams>(
                "resolve", new ResolveParams { ContractName = contractName }
            );

            if (String.IsNullOrEmpty(addrStr))
                throw new LoomException("Unable to find a contract with a matching name");

            return Address.FromString(addrStr);
        }

        /// <summary>
        /// Commits a transaction to the DAppChain.
        /// </summary>
        /// <param name="tx">Transaction to commit.</param>
        /// <param name="timeout">Specifies the amount of time after which a call will time out.</param>
        /// <returns>Commit metadata.</returns>
        /// <exception cref="InvalidTxNonceException">Thrown if transaction is rejected due to a bad nonce after <see cref="NonceRetries"/> attempts.</exception>
        internal async Task<BroadcastTxResult> CommitTxAsync(IMessage tx, int timeout = 5000)
        {
            int badNonceCount = 0;
            do
            {
                try
                {
                    try
                    {
                        Task<BroadcastTxResult> function = this.TryCommitTxAsync(tx);
                        Task result = await Task.WhenAny(function, Task.Delay(timeout));
                        if (result == function)
                        {
                            return function.Result;
                        }
                    }
                    catch (AggregateException e)
                    {
                        ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    }

                    throw new TimeoutException();
                }
                catch (InvalidTxNonceException)
                {
                    ++badNonceCount;
                }

                // WaitForSecondsRealtime can throw a "get_realtimeSinceStartup can only be called from the main thread." error.
                // WebGL doesn't have threads, so use WaitForSecondsRealtime for WebGL anyway
                const float delay = 0.5f;
#if UNITY_WEBGL && !UNITY_EDITOR
                await new WaitForSecondsRealtime(delay);
#else
                await Task.Delay(TimeSpan.FromSeconds(delay));
#endif
            } while (this.NonceRetries != 0 && badNonceCount <= this.NonceRetries);

            throw new InvalidTxNonceException(1, "sequence number does not match");
        }

        /// <summary>
        /// Queries the current state of a contract.
        /// </summary>
        /// <typeparam name="T">The expected response type, must be deserializable with Newtonsoft.Json.</typeparam>
        /// <param name="contract">Address of the contract to query.</param>
        /// <param name="query">Query parameters object.</param>
        /// <param name="caller">Optional caller address.</param>
        /// <param name="vmType">Virtual machine type.</param>
        /// <returns>Deserialized response.</returns>
        internal async Task<T> QueryAsync<T>(Address contract, IMessage query, Address caller = default(Address), VMType vmType = VMType.Plugin)
        {
            return await QueryAsync<T>(contract, query.ToByteArray(), caller, vmType);
        }

        /// <summary>
        /// Queries the current state of a contract.
        /// </summary>
        /// <typeparam name="T">The expected response type, must be deserializable with Newtonsoft.Json.</typeparam>
        /// <param name="contract">Address of the contract to query.</param>
        /// <param name="query">Raw query parameters data.</param>
        /// <param name="caller">Optional caller address.</param>
        /// <param name="vmType">Virtual machine type.</param>
        /// <returns>Deserialized response.</returns>
        internal async Task<T> QueryAsync<T>(Address contract, byte[] query, Address caller = default(Address), VMType vmType = VMType.Plugin)
        {
            if (this.readClient == null)
                throw new InvalidOperationException("Read client is not set");
            
            var queryParams = new QueryParams
            {
                ContractAddress = contract.LocalAddress,
                Params = query,
                VmType = vmType
            };
            if (caller.LocalAddress != null && caller.ChainId != null)
            {
                queryParams.CallerAddress = caller.QualifiedAddress;
            }
            await EnsureConnected();
            return await this.readClient.SendAsync<T, QueryParams>("query", queryParams);
        }

        /// <summary>
        /// Tries to commit a transaction to the DAppChain.
        /// </summary>
        /// <param name="tx">Transaction to commit.</param>
        /// <returns>Commit metadata.</returns>
        /// <exception cref="InvalidTxNonceException">Thrown when transaction is rejected by the DAppChain due to a bad nonce.</exception>
        private async Task<BroadcastTxResult> TryCommitTxAsync(IMessage tx)
        {
            if (this.writeClient == null)
                throw new InvalidOperationException("Write client was not set");
            
            await EnsureConnected();
            byte[] txBytes = tx.ToByteArray();
            if (this.TxMiddleware != null)
            {
                txBytes = await this.TxMiddleware.Handle(txBytes);
            }
            string payload = CryptoBytes.ToBase64String(txBytes);
            var result = await this.writeClient.SendAsync<BroadcastTxResult, string[]>("broadcast_tx_commit", new string[] { payload });
            if (result != null)
            {
                if (result.CheckTx.Code != 0)
                {
                    if ((result.CheckTx.Code == 1) && (result.CheckTx.Error == "sequence number does not match"))
                    {
                        throw new InvalidTxNonceException(result.CheckTx.Code, result.CheckTx.Error);
                    }
                    throw new TxCommitException(result.CheckTx.Code, result.CheckTx.Error);
                }
                if (result.DeliverTx.Code != 0)
                {
                    throw new TxCommitException(result.DeliverTx.Code, result.DeliverTx.Error);
                }
            }
            return result;
        }

        private async void SubReadClient(EventHandler<RawChainEventArgs> handler)
        {
            if (this.readClient == null)
                throw new InvalidOperationException("Read client is not set");

            try
            {
                await EnsureConnected();
                EventHandler<JsonRpcEventData> wrapper = (sender, e) =>
                {
                    handler(this, new RawChainEventArgs
                    (
                        e.ContractAddress,
                        e.CallerAddress,
                        UInt64.Parse(e.BlockHeight), 
                        e.Data,
                        e.Topics
                    ));
                };
                this.eventSubs.Add(handler, wrapper);
                await this.readClient.SubscribeAsync(wrapper);
            }
            catch (Exception e)
            {
                Logger.Log(LogTag, e.Message);
            }
        }

        private async void UnsubReadClient(EventHandler<RawChainEventArgs> handler)
        {
            if (this.readClient == null)
                throw new InvalidOperationException("Read client is not set");

            try
            {
                EventHandler<JsonRpcEventData> wrapper = this.eventSubs[handler];
                await this.readClient.UnsubscribeAsync(wrapper);
            }
            catch (Exception e)
            {
                Logger.Log(LogTag, e.Message);
            }
        }
        
        private async Task EnsureConnected()
        {
            if (!this.AutoReconnect)
                return;

            if (this.readClient != null)
            {
                await EnsureConnected(this.readClient);
            }

            if (this.writeClient != null)
            {
                await EnsureConnected(this.writeClient);
            }
        }

        private async Task EnsureConnected(IRpcClient rpcClient) {
            // TODO: handle edge-case when ConnectionState == RpcConnectionState.Connecting
            if (rpcClient.ConnectionState != RpcConnectionState.Connected)
            {
                await rpcClient.ConnectAsync();
            }
        }

        private struct NonceParams
        {
            [JsonProperty("key")]
            public string Key;
        }

        private struct ResolveParams
        {
            [JsonProperty("name")]
            public string ContractName;
        }

        private class QueryParams
        {
            /// <summary>
            /// Contract address
            /// </summary>
            [JsonProperty("contract")]
            public string ContractAddress;

            /// <summary>
            /// Serialized protobuf of contract-specific query parameters
            /// </summary>
            [JsonProperty("query")]
            public byte[] Params;

            /// <summary>
            /// Optional caller address (including chain ID)
            /// </summary>
            [JsonProperty("caller")]
            public string CallerAddress;

            /// <summary>
            /// Virtual machine type.
            /// </summary>
            [JsonProperty("vmType")]
            public VMType VmType;
        }
    }
}