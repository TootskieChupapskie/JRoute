## Running the Project with Docker

This project is containerized using Docker and Docker Compose for easy setup and deployment. Below are the instructions and requirements specific to this project:

### Requirements
- **.NET Version:** 9.0 (as specified in the Dockerfile)
- **Docker** and **Docker Compose** installed on your system

### Build and Run Instructions
1. **Clone the repository** and navigate to the project root.
2. **Build and start the service:**
   ```sh
   docker compose up --build
   ```
   This will build the Docker image using the provided `Dockerfile` and start the `csharp-jroute` service.

### Environment Variables
- No required environment variables are specified by default.
- If you need to set environment variables, you can create a `.env` file and uncomment the `env_file` line in the `docker-compose.yml`.

### Ports
- **No ports are exposed by default.**
  - If your application listens on a specific port, uncomment and set the `ports` section in `docker-compose.yml` and the `EXPOSE` line in the `Dockerfile` as needed.

### Special Configuration
- The Dockerfile creates a non-root user (`jroute`) for running the application, enhancing security.
- The build process uses Docker build cache for NuGet packages and MSBuild to speed up subsequent builds.
- If your application depends on external services (e.g., PostgreSQL, Redis), you can define them in the `docker-compose.yml` as shown in the commented examples.

### Customization
- To expose a port (e.g., 80), edit `docker-compose.yml`:
  ```yaml
  ports:
    - "80:80"
  ```
  And uncomment the `EXPOSE 80` line in the `Dockerfile` if needed.
- For additional services or persistent storage, follow the commented examples in the compose file.

---

_This section is up to date with the current Docker setup. Update as needed if you add new services or configuration._
