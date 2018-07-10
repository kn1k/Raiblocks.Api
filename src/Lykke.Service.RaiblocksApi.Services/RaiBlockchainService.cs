﻿using Lykke.Service.RaiblocksApi.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using RaiBlocks;
using RaiBlocks.Actions;
using RaiBlocks.ValueObjects;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using Lykke.Service.RaiblocksApi.Core.Domain.Entities.Transactions;
using Lykke.Service.RaiblocksApi.Services.Models;

namespace Lykke.Service.RaiblocksApi.Services
{
    public class RaiBlockchainService : IBlockchainService
    {
        private readonly RaiBlocksRpc _raiBlocksRpc;
        private readonly IAssetService _assetService;

        private const int retryCount = 4;

        private const int retryTimeout = 1;

        private Policy policy = null;

        public RaiBlockchainService(RaiBlocksRpc raiBlocksRpc, IAssetService assetService)
        {
            _raiBlocksRpc = raiBlocksRpc;
            _assetService = assetService;

            policy = Policy
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(retryCount, retryAttempt => TimeSpan.FromSeconds(retryTimeout));
        }

        /// <summary>
        /// Simple check validity for signed transaction
        /// </summary>
        /// <param name="signedTransaction"></param>
        /// <returns>Validity</returns>
        public bool IsSignedTransactionValid(string signedTransaction)
        {
            try
            {
                // TODO: make better
                var a = JObject.Parse(signedTransaction);
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        public async Task<string> CreateUnsignSendTransactionAsync(string address, string destination, string amount)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var raiAddress = new RaiAddress(address);
                var raiDestination = new RaiAddress(destination);
                var accountInfo = await _raiBlocksRpc.GetAccountInformationAsync(raiAddress);

                return await Task.Run(async () =>
                {
                    var txContext = JObject.FromObject(new BlockCreate
                    {
                        Type = BlockType.send,
                        AccountNumber = raiAddress,
                        Destination = raiDestination,
                        Balance = accountInfo.Balance,
                        Amount = new RaiUnits.RaiRaw(amount),
                        Previous = accountInfo.Frontier
                    });
                    var work = await _raiBlocksRpc.GetWorkAsync(accountInfo.Frontier);

                    txContext.Add("work", work.Work);

                    return txContext.ToString();
                });
            });

            return await policyResult;
        }


        public async Task<string> CreateUnsignReceiveTransactionAsync(string sendTransactionHash)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var retrieveBlock = await _raiBlocksRpc.GetRetrieveBlockAsync(sendTransactionHash);

                var destination = retrieveBlock?.Contents?.Destination;
                var raiDestination = new RaiAddress(destination);
                var accountInfo = await _raiBlocksRpc.GetAccountInformationAsync(raiDestination);

                if (accountInfo.Error != null &&
                    accountInfo.Error.Equals("Account not found", StringComparison.InvariantCultureIgnoreCase))
                {
                    return await Task.Run(async () =>
                    {
                        var txContext = JObject.FromObject(new BlockCreate
                        {
                            Type = BlockType.open,
                            AccountNumber = destination,
                            RepresentativeNumber = destination,
                            Source = sendTransactionHash
                        });
                        var work = await _raiBlocksRpc.GetWorkAsync(
                            (await _raiBlocksRpc.GetAccountKeyAsync(raiDestination)).Key);

                        txContext.Add("work", work.Work);

                        return txContext.ToString();
                    });
                }
                else
                {
                    return await Task.Run(async () =>
                    {
                        var txContext = JObject.FromObject(new BlockCreate
                        {
                            Type = BlockType.receive,
                            AccountNumber = destination,
                            Source = sendTransactionHash,
                            Previous = accountInfo.Frontier
                        });
                        var work = await _raiBlocksRpc.GetWorkAsync(accountInfo.Frontier);

                        txContext.Add("work", work.Work);

                        return txContext.ToString();
                    });
                }
            });

            return await policyResult;
        }

        public async Task<Dictionary<string, string>> GetAddressBalancesAsync(IEnumerable<string> balanceObservation)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                IEnumerable<RaiAddress> accounts = balanceObservation.Select(x => new RaiAddress(x));
                var result = await _raiBlocksRpc.GetBalancesAsync(accounts);
                return result.Balances.ToDictionary(x => x.Key, x => x.Value.Balance.ToString());
            });

            return await policyResult;
        }

        public async Task<string> GetAddressBalanceAsync(string address)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var result = await _raiBlocksRpc.GetBalanceAsync(new RaiAddress(address));
                return result.Balance.ToString();
            });

            return await policyResult;
        }

        public async Task<bool> IsAddressValidAsync(string address)
        {
            try
            {
                var policyResult = policy.ExecuteAsync(async () =>
                {
                    var result = await _raiBlocksRpc.ValidateAccountAsync(new RaiAddress(address));
                    return result.IsValid();
                });

                return await policyResult;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        public async Task<long> GetAddressBlockCountAsync(string address)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var result = await _raiBlocksRpc.GetAccountBlockCountAsync(new RaiAddress(address));
                return result.BlockCount;
            });

            return await policyResult;
        }

        public async Task<(string hash, string error)> BroadcastSignedTransactionAsync(string signedTransaction)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var result = await _raiBlocksRpc.ProcessBlockAsync(signedTransaction);
                return (result.Hash, result.Error);
            });

            return await policyResult;
        }

        public async Task<IEnumerable<(string from, string to, BigInteger amount, string hash, TransactionType type)>>
            GetAddressHistoryAsync(string address, int take)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var result = await _raiBlocksRpc.GetAccountHistoryAsync(new RaiAddress(address), take);
                return result.Entries.Select(x =>
                {
                    if (x.Type == BlockType.send)
                    {
                        return (address, x.RepresentativeBlock, x.Amount.Value, x.Frontier, TransactionType.send);
                    }
                    else if (x.Type == BlockType.receive)
                    {
                        return (x.RepresentativeBlock, address, x.Amount.Value, x.Frontier, TransactionType.receive);
                    }
                    else
                    {
                        throw new Exception("Unknown history type");
                    }
                });
            });

            return await policyResult;
        }

        public async Task<(string frontier, long blockCount)> GetAddressInfoAsync(string address)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var accountInfo = await _raiBlocksRpc.GetAccountInformationAsync(new RaiAddress(address));
                return (accountInfo.Frontier, accountInfo.BlockCount);
            });

            return await policyResult;
        }

        public async Task<ITransactionMeta> GetTransactionMetaAsync(string hash)
        {
            var policyResult = policy.ExecuteAsync(async () =>
            {
                var retrieveBlock =
                    (await _raiBlocksRpc.GetRetrieveBlocksInfoAsync(new List<string> {hash})).Blocks[hash];
                return new TransactionMeta
                {
                    Amount = retrieveBlock.Amount,
                    AssetId = _assetService.AssetId,
                    FromAddress = retrieveBlock.BlockAccount,
                    ToAddress = retrieveBlock.Contents.Destination,
                    Hash = hash,
                    IncludeFee = false,
                    TransactionType =
                        retrieveBlock.Contents.Type == BlockType.send ? TransactionType.send : TransactionType.receive
                };
            });

            return await policyResult;
        }

        public bool IsAddressValidOfflineAsync(string address)
        {
            try
            {
                var raiAddress = new RaiAddress(address);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
