#pragma once
#include <windows.h>

#pragma pack(push, 4)

struct SPageFilePhysics {
    int packetId;
    float gas;
    float brake;
    float fuel;
    int gear;
    int rpms;
    float steerAngle;
    float speedKmh;
    float velocity[3];
    float accG[3];
    float wheelSlip[4];
    float wheelLoad[4];
    float wheelsPressure[4];
    float wheelAngularSpeed[4];
    float tyreWear[4];
    float tyreDirtyLevel[4];
    float tyreCoreTemperature[4];
    float cameroPosition[3];
    float airDensity;
    float airTemp;
    float roadTemp;
    float localAngularVelocity[3];
    float finalFFB;
    float performanceMeter;
    int engineBraking;
    int ersRecoveryLevel;
    int ersPowerLevel;
    int ersHeatCharging;
    int ersIsCharging;
    float kersCurrentKJ;
    int drsAvailable;
    int drsEnabled;
    float brakeTemp[4];
    float clutch;
    float tyreTempI[4];
    float tyreTempM[4];
    float tyreTempO[4];
    int isAIControlled;
    float tyreContactPoint[4][3];
    float tyreContactNormal[4][3];
    float tyreContactHeading[4][3];
    float brakeBias;
    float localVelocity[3];
};

struct SPageFileGraphic {
    int packetId;
    int status;
    int session;
    wchar_t currentTime[15];
    wchar_t lastTime[15];
    wchar_t bestTime[15];
    wchar_t split[15];
    int completedLaps;
    int position;
    int iCurrentTime;
    int iLastTime;
    int iBestTime;
    float sessionTimeLeft;
    float distanceTraveled;
    int isInPit;
    int currentSectorIndex;
    int lastSectorTime;
    int numberOfLaps;
    wchar_t tyreCompound[33];
    float replayTimeMultiplier;
    float normalizedCarPosition;

    int activeCars;
    float carCoordinates[60][3];
    int carID[60];
    int playerCarID;
    float penaltyTime;
    float flag;
    int penalty;
    int idealLineOn;
    int isInPitLane;

    float surfaceGrip;
    int mandatoryPitDone;
    float windSpeed;
    float windDirection;
    int isSetupMenuVisible;
    int mainRedFlag;
    int yellowFlag;
    int hazardFlag;
    int blackFlag;
};

#pragma pack(pop)