using UserManagementApi.Domain.Entities;

namespace UserManagement.Domain.Interfaces
{
    public interface IUserService
    {
        Task<User?> GetUserByEmailAsync(string email);
        Task<User> CreateUserAsync(User user);
        Task<List<User>> GetAllUsersAsync();
    }
}
