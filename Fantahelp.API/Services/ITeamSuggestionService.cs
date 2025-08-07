namespace Fantahelp.API.Services
{
    public interface ITeamSuggestionService
    {
        Task<ServiceResult<List<SuggestionResult>>> GetOptimalTeamSuggestionAsync(SuggestionRequest suggestionRequest);
    }
}