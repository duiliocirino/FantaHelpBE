using Microsoft.AspNetCore.Mvc;
using Fantahelp.API.Services;

namespace Fantahelp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;

        public UsersController(IUserService userService)
        {
            _userService = userService;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserReadDto>>> GetAllUsers()
        {
            var result = await _userService.GetAllUsersAsync();
            var users = result.Data;

            if (users == null || !users.Any())
                return NoContent();

            var userDtos = users.Select(u => UserMapper.ToReadDto(u));
            return Ok(userDtos);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<UserReadDto>> GetUserById(int id)
        {
            var result = await _userService.GetUserByIdAsync(id);
            var user = result.Data;

            if (user == null)
                return NotFound();

            var userReadDto = UserMapper.ToReadDto(user);
            return Ok(userReadDto);
        }

        [HttpPost]
        public async Task<ActionResult<UserReadDto>> CreateUser([FromBody] UserCreateDto userCreateDto)
        {
            var result = await _userService.CreateUserAsync(userCreateDto);

            if (!result.Success || result.Data == null)
                return BadRequest(result.ErrorMessage);

            var userReadDto = UserMapper.ToReadDto(result.Data);

            return CreatedAtAction(nameof(GetUserById), new { id = result.Data.Id }, userReadDto);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var result = await _userService.DeleteUserAsync(id);

            if (!result.Success)
                return NotFound(result.ErrorMessage);

            return NoContent();
        }
    }
}