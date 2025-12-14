# Cimian Testing Container
# Windows Server Core with Go and C# binaries for comparison testing

FROM mcr.microsoft.com/windows/servercore:ltsc2022

SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

# Create directories
RUN New-Item -ItemType Directory -Path 'C:\Cimian\Go' -Force | Out-Null; \
    New-Item -ItemType Directory -Path 'C:\Cimian\CSharp' -Force | Out-Null; \
    New-Item -ItemType Directory -Path 'C:\Cimian\Tests' -Force | Out-Null; \
    New-Item -ItemType Directory -Path 'C:\Cimian\Repo' -Force | Out-Null; \
    New-Item -ItemType Directory -Path 'C:\Cimian\Results' -Force | Out-Null

# Copy Go binaries (from host build)
COPY go-binaries/ C:/Cimian/Go/

# Copy C# binaries (from host build)  
COPY csharp-binaries/ C:/Cimian/CSharp/

# Copy test scripts
COPY test-scripts/ C:/Cimian/Tests/

# Set working directory
WORKDIR C:/Cimian

# Default command - run comparison tests
CMD ["powershell", "-File", "C:\\Cimian\\Tests\\Run-DockerTests.ps1"]
