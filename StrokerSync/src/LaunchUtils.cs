using System;
using UnityEngine;

namespace StrokerSync
{
    public static class LaunchUtils
    {
        public const float LAUNCH_MAX_VAL = 1.0f;
        public const float LAUNCH_MIN_SPEED = 0.1f;
        public const float LAUNCH_MAX_SPEED = 1.0f;

        public static float PredictMoveSpeed(float prevPos, float currPos, float durationSecs)
        {
            double durationNanoSecs = durationSecs * 1e9;
            double delta = currPos - prevPos;
            double dist = Math.Abs(delta);

            if (dist < 0.01)
                return LAUNCH_MIN_SPEED;

            double mil = (durationNanoSecs / 1e6) * 90 / (dist * 100);
            double speed = 25000.0 * Math.Pow(mil, -1.05);

            float normalizedSpeed = (float)(speed / 100.0);
            return Mathf.Clamp(normalizedSpeed, LAUNCH_MIN_SPEED, LAUNCH_MAX_SPEED);
        }

        public static float PredictMoveDuration(float dist, float speed)
        {
            if (dist <= 0.0f)
                return 0.0f;

            double speedScaled = speed * 100.0;
            double distScaled = dist * 100.0;

            double mil = Math.Pow(speedScaled / 25000, -0.95);
            double dur = (mil / (90 / distScaled)) / 1000;
            return (float)dur;
        }

        public static float PredictDistanceTraveled(float speed, float durationSecs)
        {
            if (speed <= 0.0f)
                return 0.0f;

            double durationNanoSecs = durationSecs * 1e9;
            double speedScaled = speed * 100.0;

            double mil = Math.Pow(speedScaled / 25000, -0.95);
            double diff = mil - durationNanoSecs / 1e6;
            double dist = 90 - (diff / mil * 90);

            return (float)(dist / 100.0);
        }

        public static int CalculateTimeMs(float distance, float velocity)
        {
            if (velocity <= 0.0f)
                return 1000;

            float duration = PredictMoveDuration(distance, velocity);
            return Mathf.Max(20, (int)(duration * 1000));
        }
    }
}
