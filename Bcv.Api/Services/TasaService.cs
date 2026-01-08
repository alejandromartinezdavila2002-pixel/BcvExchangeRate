using Bcv.Shared;
using Supabase;

namespace Bcv.Api.Services
{
    public class TasaService : ITasaService
    {
        private readonly Supabase.Client _supabase;

        public TasaService(Supabase.Client supabase)
        {
            _supabase = supabase;
        }

        public async Task<TasaBcv?> GetUltimaTasaAsync()
        {
            var respuesta = await _supabase.From<TasaBcv>()
                .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                .Limit(1)
                .Get();

            return respuesta.Models.FirstOrDefault();
        }

        // IMPLEMENTACIÓN DE LA VALIDACIÓN
        public async Task<bool> EsClienteValido(string apiKey)
        {
            try
            {
                // Buscamos en la tabla clientes_api una coincidencia con la llave
                var resultado = await _supabase.From<ClienteApi>()
                    .Where(x => x.ApiKey == apiKey)
                    .Get();

                var cliente = resultado.Models.FirstOrDefault();

                // Retorna true solo si el cliente existe y está marcado como aprobado
                return cliente != null && cliente.EstaAprobado;
            }
            catch
            {
                // Si hay un error de conexión, por seguridad denegamos el acceso
                return false;
            }
        }
    }
}