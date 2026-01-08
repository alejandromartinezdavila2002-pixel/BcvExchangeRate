using Bcv.Shared;

namespace Bcv.Api.Services
{
    public interface ITasaService
    {
        Task<TasaBcv?> GetUltimaTasaAsync();

        // Nuevo método para validar la API Key
        Task<bool> EsClienteValido(string apiKey);
    }


}