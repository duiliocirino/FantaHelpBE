from fastapi import APIRouter
from leagues.service import create_league

router = APIRouter()

@router.post("/leagues", response_model=dict)
def create_new_league():
    """
    Endpoint to create a new league.
    
    :return: A dictionary containing the league details
    """
    # Example data, replace with actual data from request
    name = "New League"
    player_ids = [1, 2, 3]
    squad_ids = [101, 102]
    credits = 800
    
    league = create_league(name=name, player_ids=player_ids, squad_ids=squad_ids, credits=credits)
    
    return {
        "name": league.name,
        "player_ids": league.player_ids,
        "squad_ids": league.squad_ids,
        "credits": league.credits
    }
