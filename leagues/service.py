from leagues.models import League

def create_league(name: str, player_ids: list, squad_ids: list, credits: int = 800) -> League:
    """
    Create a new league with the given parameters.
    
    :param name: Name of the league
    :param player_ids: List of player IDs in the league
    :param squad_ids: List of squad IDs in the league
    :param credits: Initial credits for the league (default is 800)
    :return: An instance of League with the provided details
    """
    league = League()
    league.name = name
    league.player_ids = player_ids
    league.squad_ids = squad_ids
    league.credits = credits
    
    return league
