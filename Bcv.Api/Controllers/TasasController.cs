using Microsoft.AspNetCore.Mvc;
using Bcv.Shared;
using Supabase;
using Postgrest;

namespace Bcv.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TasasController : ControllerBase
    {
        private readonly Supabase.Client _supabase;

        public TasasController(Supabase.Client supabase)
        {
            _supabase = supabase;
        }

        // Endpoint para obtener la última tasa registrada
        [HttpGet("ultima")]
        public async Task<ActionResult<TasaBcv>> GetUltimaTasa()
        {
            try
            {
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1)
                    .Get();

                var tasa = respuesta.Models.FirstOrDefault();

                if (tasa == null)
                {
                    return NotFound(new { mensaje = "No hay tasas registradas en la base de datos." });
                }

                return Ok(tasa);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al consultar Supabase", detalle = ex.Message });
            }
        }
    }
}