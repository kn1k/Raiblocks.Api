﻿using System.Threading.Tasks;

namespace Lykke.Service.RaiblocksApi.Core.Services
{
    public interface IShutdownManager
    {
        Task StopAsync();
    }
}