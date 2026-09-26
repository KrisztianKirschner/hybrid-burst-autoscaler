using Microsoft.AspNetCore.Mvc;

namespace Hba.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TestController : ControllerBase
    {
        // GET: api/Test
        [HttpGet]
        public IActionResult Get()
        {
            return Ok("Hello from hba-api");
        }
    }
}
