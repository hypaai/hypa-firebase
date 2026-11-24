# Submission work

New work done for the Firebase hackathon. Imported work goes in `docs/pre-existing-work.md`.

## G02 (HYP-197), part 1: REST auth and App Check for Quest

Fallback for Quest in case the Firebase Unity SDK does not run there. Code in `Assets/HypaFirebase/Runtime/Rest/`.

- `RestAuthSession`: `IAuthSession` over the Identity Toolkit and Secure Token REST APIs. Signs in anonymously on first launch and brings the same user back later using a saved refresh token. If the saved token is dead (`INVALID_REFRESH_TOKEN`, `TOKEN_EXPIRED`, `USER_NOT_FOUND`), it clears the token and signs in as a new user. A refresh that finishes after `SignOut` is ignored.
- `RestAppCheck`: swaps an App Check debug token (registered in the Firebase console) for an App Check token, for Quest and the editor.
- `IRefreshTokenStore` with a PlayerPrefs version, and `HypaFirebaseException`.
- No token is logged. The web API key and app id are passed in, not committed.
- Tests: EditMode tests in `Assets/HypaFirebase/Tests/Editor/RestAuthTests.cs`, using a fake HTTP handler.
- Not yet tested against real Firebase or on a device.
