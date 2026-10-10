using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PanaszKezelo.Api.Data;
using PanaszKezelo.Api.Models;

namespace PanaszKezelo.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RequirementsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public RequirementsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/requirements
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Nis2Requirement>>> GetRequirements()
        {
            return await _context.Nis2Requirements.ToListAsync();
        }

        // GET: api/requirements/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Nis2Requirement>> GetRequirement(int id)
        {
            var requirement = await _context.Nis2Requirements.FindAsync(id);

            if (requirement == null)
            {
                return NotFound(new { message = $"A(z) {id} azonosítójú követelmény nem található." });
            }

            return requirement;
        }
    }
}