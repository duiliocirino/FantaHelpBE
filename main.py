from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

#from settings import settings

from leagues.endpoints import router as leagues_router

# Initialize FastAPI application
app = FastAPI(
    title="FantaHelp API",
    description="API for managing fantasy leagues and players",
    version="0.1.0",
)

# Configure CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Allows all origins, adjust as needed
    allow_credentials=True,
    allow_methods=["*"],  # Allows all methods, adjust as needed
    allow_headers=["*"],  # Allows all headers, adjust as needed
)

# Include routers with prefixes and tags
app.include_router(leagues_router, prefix="/api/leagues", tags=["Leagues"])

@app.get("/health", tags=["Health"])
async def health_check():
    return {"status": "ok"}

@app.get("/", tags=["Root"])
async def root():
    return {"message": "Welcome to the FantaHelp API. Use /docs for API documentation."}

# To run the application, use the command:
# uvicorn main:app --reload
# This will start the FastAPI server with live reloading enabled.
# The server will be accessible at http://localhost:8000 by default.
# You can access the API documentation at http://localhost:8000/docs.
# The API will be available at http://localhost:8000/api/leagues
# The health check endpoint will be available at http://localhost:8000/health.
# The root endpoint will be available at http://localhost:8000/
# The root endpoint will return a welcome message.
# The health check endpoint will return a status message.
# The leagues router will handle all league-related endpoints under the /api/leagues prefix.
# The leagues router will be tagged with "Leagues" for better organization in the API documentation.
# The application is configured to allow CORS from all origins, which is suitable for development.
# Adjust the CORS settings as needed for production environments.
# The application is set up to run with Uvicorn, a lightning-fast ASGI server.
# Make sure to install the required dependencies in your environment:
# pip install fastapi uvicorn
# This will install FastAPI and Uvicorn, which are necessary to run the application.
# You can also install additional dependencies as needed for your project.
# Ensure that you have a proper Python environment set up with the necessary packages installed.
# For more information on FastAPI, refer to the official documentation at https://fastapi.tiangolo.com/.
# For more information on Uvicorn, refer to the official documentation at https://www.uvicorn.org/.
# For more information on CORS, refer to the FastAPI documentation at https://fastapi.tiangolo.com/tutorial/cors/.
# For more information on settings management, refer to the Pydantic documentation at https://docs.pydantic.dev/latest/.
# For more information on the project structure, refer to the project's README file.
# For more information on how to contribute to the project, refer to the CONTRIBUTING.md file.
# For more information on how to report issues, refer to the project's ISSUE_TEMPLATE.md file.
# For more information on how to run tests, refer to the project's TESTING.md file.
# For more information on how to deploy the application, refer to the DEPLOYMENT.md file.
# For more information on how to use the API, refer to the API documentation at http://localhost:8000/docs.
# For more information on how to use the API, refer to the OpenAPI specification at http://localhost:8000/openapi.json.
# For more information on how to use the API, refer to the Swagger UI at http://localhost:8000/docs.
# For more information on how to use the API, refer to the ReDoc documentation at http://localhost:8000/redoc.
# For more information on how to use the API, refer to the API reference at http://localhost:8000/api/leagues.
# For more information on how to use the API, refer to the API documentation at http://localhost:8000/api/leagues/docs.