using Bcv.Api.Services;
using Bcv.Shared;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class TasasController : ControllerBase
{
    private readonly ITasaService _tasaService;

    public TasasController(ITasaService tasaService)
    {
        _tasaService = tasaService;
    }

    [HttpGet("ultima")]
    public async Task<ActionResult<TasaBcv>> GetUltimaTasa()
    {
        try
        {
            var tasa = await _tasaService.GetUltimaTasaAsync();
            if (tasa == null) return NotFound();

            return Ok(tasa);
        }
        catch (Exception ex)
        {
            // Aquí podrías usar un Logger profesional más adelante
            return StatusCode(500, new { error = "Ocurrió un error interno al procesar la solicitud." });
        }
    }
}