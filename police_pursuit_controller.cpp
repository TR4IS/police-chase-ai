#include <iostream>
#include <windows.h>
#include <cmath>
#include "SharedMemory.h"

// Constants
const float SPEED_THRESHOLD_KMH = 80.0f;
const float ARREST_DISTANCE_METERS = 5.0f;

// Shared Memory File Handles
HANDLE hPhysicsMap = NULL;
HANDLE hGraphicsMap = NULL;
SPageFilePhysics* pPhysics = NULL;
SPageFileGraphic* pGraphics = NULL;

bool InitSharedMemory() {
    hPhysicsMap = OpenFileMappingA(FILE_MAP_READ, FALSE, "Local\\acpmf_physics");
    if (!hPhysicsMap) {
        std::cout << "[ERROR] Could not open acpmf_physics Shared Memory!" << std::endl;
        return false;
    }
    pPhysics = (SPageFilePhysics*)MapViewOfFile(hPhysicsMap, FILE_MAP_READ, 0, 0, sizeof(SPageFilePhysics));

    hGraphicsMap = OpenFileMappingA(FILE_MAP_READ, FALSE, "Local\\acpmf_graphics");
    if (!hGraphicsMap) {
        std::cout << "[ERROR] Could not open acpmf_graphics Shared Memory!" << std::endl;
        return false;
    }
    pGraphics = (SPageFileGraphic*)MapViewOfFile(hGraphicsMap, FILE_MAP_READ, 0, 0, sizeof(SPageFileGraphic));

    std::cout << "[SUCCESS] Connected to Assetto Corsa Shared Memory Pipeline!" << std::endl;
    return true;
}

void ShutdownSharedMemory() {
    if (pPhysics) UnmapViewOfFile(pPhysics);
    if (hPhysicsMap) CloseHandle(hPhysicsMap);
    if (pGraphics) UnmapViewOfFile(pGraphics);
    if (hGraphicsMap) CloseHandle(hGraphicsMap);
}

int main() {
    std::cout << "==========================================================" << std::endl;
    std::cout << "  Native C++ AI Police Pursuit Controller v1.0" << std::endl;
    std::cout << "==========================================================" << std::endl;

    while (!InitSharedMemory()) {
        std::cout << "Waiting for Assetto Corsa session to start..." << std::endl;
        Sleep(2000);
    }

    bool pursuitActive = false;

    while (true) {
        Sleep(16); // 60 Hz update loop

        if (!pPhysics || !pGraphics) continue;

        float playerSpeed = pPhysics->speedKmh;
        int activeCars = pGraphics->activeCars;
        int playerID = pGraphics->playerCarID;

        // Player 3D Coordinates
        float pX = pGraphics->carCoordinates[playerID][0];
        float pY = pGraphics->carCoordinates[playerID][1];
        float pZ = pGraphics->carCoordinates[playerID][2];

        // Trigger Pursuit on Speeding (> 80 km/h)
        if (playerSpeed > SPEED_THRESHOLD_KMH && !pursuitActive) {
            pursuitActive = true;
            std::cout << "🚨 [PURSUIT ACTIVATED] High Speed Infraction: " << playerSpeed << " km/h!" << std::endl;
        }

        if (pursuitActive) {
            for (int i = 0; i < activeCars; i++) {
                if (i == playerID) continue; // Skip Player car

                float cX = pGraphics->carCoordinates[i][0];
                float cY = pGraphics->carCoordinates[i][1];
                float cZ = pGraphics->carCoordinates[i][2];

                // Calculate Distance Vector
                float dX = pX - cX;
                float dZ = pZ - cZ;
                float distance = std::sqrt(dX * dX + dZ * dZ);

                // Calculate Pursuit Steering Angle Delta
                float targetAngle = std::atan2(dX, dZ);
                float steerInput = std::clamp(targetAngle * 2.0f, -1.0f, 1.0f);

                // Output Control Telemetry
                if (distance <= ARREST_DISTANCE_METERS) {
                    std::cout << "🚨 [BUSTED] AI Cop Car ID " << i << " pinned Player!" << std::endl;
                    pursuitActive = false;
                }
            }
        }
    }

    ShutdownSharedMemory();
    return 0;
}