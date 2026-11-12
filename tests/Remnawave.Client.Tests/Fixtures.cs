namespace Remnawave.Client.Tests;

internal static class Fixtures
{
    public const string UserJson = """
        {
          "response": {
            "id": 42,
            "uuid": "8f6b4a8e-3a51-4c0e-9d1b-3b6b0f1f2c11",
            "shortUuid": "abcd1234",
            "username": "alice",
            "status": "ACTIVE",
            "trafficLimitBytes": 107374182400,
            "trafficLimitStrategy": "MONTH_ROLLING",
            "expireAt": "2026-12-31T23:59:59.000Z",
            "telegramId": 123456789,
            "email": null,
            "subscriptionUrl": "https://sub.example.com/abcd1234",
            "activeInternalSquads": [ { "uuid": "4d3b0d7e-0e7d-4c84-8f43-0c8b8a7d3a10", "name": "Default" } ],
            "userTraffic": { "usedTrafficBytes": 5368709120, "lifetimeUsedTrafficBytes": 10737418240, "onlineAt": null },
            "someFutureField": "kept in AdditionalData"
          }
        }
        """;
}
