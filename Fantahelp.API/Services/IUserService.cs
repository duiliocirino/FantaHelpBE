namespace Fantahelp.API.Services
{
    public interface IUserService
    {
        Task<ServiceResult<IEnumerable<User>>> GetAllUsersAsync();
        Task<ServiceResult<User?>> GetUserByIdAsync(int id);
        Task<ServiceResult<User>> CreateUserAsync(UserCreateDto user);
        Task<ServiceResult<bool>> DeleteUserAsync(int userId);
    }
}