class League:
    def __init__(self):
        self.player_ids: list = []
        self.squad_ids: list = []
        self.credits: int = 800
        self.name: str = ""
        self.id: int = 0

    def __str__(self):
        return f"League(name={self.name}, id={self.id}, credits={self.credits}, player_ids={self.player_ids}, squad_ids={self.squad_ids})"
