# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:9.0

# Install Java JDK for Android development
RUN apt-get update && \
    apt-get install -y openjdk-17-jdk wget unzip ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Install Android SDK
ENV ANDROID_HOME=/opt/android-sdk
ENV PATH=$PATH:$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools

RUN mkdir -p $ANDROID_HOME/cmdline-tools && \
    wget -q https://dl.google.com/android/repository/commandlinetools-linux-9477386_latest.zip -O /tmp/cmdline-tools.zip && \
    unzip -q /tmp/cmdline-tools.zip -d $ANDROID_HOME/cmdline-tools && \
    mv $ANDROID_HOME/cmdline-tools/cmdline-tools $ANDROID_HOME/cmdline-tools/latest && \
    rm /tmp/cmdline-tools.zip

# Accept licenses and install necessary Android packages
RUN yes | sdkmanager --licenses && \
    sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"

# ✅ INSTALL ONLY THE ANDROID WORKLOAD (NOT 'maui')
RUN dotnet workload install maui-android --skip-manifest-update

# Set working directory
WORKDIR /app

# Copy source code
COPY . .

# Create a script for easy building
RUN echo '#!/bin/bash\n\
echo "Building JRoute Android App..."\n\
dotnet restore\n\
dotnet build -c Release -f net9.0-android\n\
echo "Build complete!"\n\
echo ""\n\
echo "To publish APK, run:"\n\
echo "dotnet publish -c Release -f net9.0-android"\n\
echo ""\n\
echo "APK will be in: bin/Release/net9.0-android/publish/"\n\
' > /app/build.sh && chmod +x /app/build.sh

# Default command shows instructions
CMD ["/bin/bash", "-c", "echo 'JRoute Development Environment Ready!' && echo '' && echo 'Available commands:' && echo '  ./build.sh                    - Build the project' && echo '  dotnet build                  - Standard build' && echo '  dotnet publish -c Release -f net9.0-android - Create APK' && echo '' && echo 'Starting interactive shell...' && /bin/bash"]