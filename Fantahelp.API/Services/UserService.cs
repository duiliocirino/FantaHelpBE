using Microsoft.EntityFrameworkCore;

namespace Fantahelp.API.Services
{
    public class UserService : IUserService
    {
        private readonly FantahelpContext _context;

        public UserService(FantahelpContext context)
        {
            _context = context;
        }

        public async Task<ServiceResult<IEnumerable<User>>> GetAllUsersAsync()
        {
            var users = await _context.Users.ToListAsync();
            return ServiceResult<IEnumerable<User>>.SuccessResult(users);
        }

        public async Task<ServiceResult<User?>> GetUserByIdAsync(int id)
        {
            var user = await _context.Users.FindAsync(id);

            if (user == null)
                return ServiceResult<User?>.FailureResult("No User was found with the given Id.");

            return ServiceResult<User?>.SuccessResult(user);
        }

        public async Task<ServiceResult<User>> CreateUserAsync(UserCreateDto user)
        {
            var new_user = new User
            {
                UserName = user.UserName,
                Email = user.Email,
                Password = user.Password,
                IsFake = user.IsFake
            };

            await _context.AddAsync(new_user);
            await _context.SaveChangesAsync();

            return ServiceResult<User>.SuccessResult(new_user);        }

        public async Task<ServiceResult<bool>> DeleteUserAsync(int userId)
        {

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return ServiceResult<bool>.FailureResult("No User is associated to the given userId.");

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return ServiceResult<bool>.SuccessResult(true);
        }
    }
}