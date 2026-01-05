using Microsoft.AspNetCore.Mvc;
using Bcv.Shared;
using Supabase;

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

        [HttpGet("ultima")]
        public async Task<ActionResult<TasaBcv>> GetUltimaTasa()
        {
            try
            {
                // Al heredar TasaBcv de BaseModel, este .From<TasaBcv>() ya no dará error
                var respuesta = await _supabase.From<TasaBcv>()
                    .Order("creado_el", Postgrest.Constants.Ordering.Descending)
                    .Limit(1)
                    .Get();

                var tasa = respuesta.Models.FirstOrDefault();

                if (tasa == null) return NotFound();

                return Ok(tasa); // Gracias al [JsonIgnore], esto ya no dará Error 500
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}