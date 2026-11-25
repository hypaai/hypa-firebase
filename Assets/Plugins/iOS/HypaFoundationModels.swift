// Apple's on-device model (Foundation Models, iOS 26+) for Unity: AppleFoundationModel.cs calls these C functions
// (docs/ios-on-device.md). Everything here runs on the main thread, which is Unity's thread on iOS, so replies reach C#
// on the thread that asked. The framework is weak-linked (HypaIosBuild.cs), so the app still runs on iOS 15 to 18,
// where the status is "OS too old".
import Foundation
#if canImport(FoundationModels)
import FoundationModels
#endif

/// (request id, kind, text). Kinds: 0 chunk, 1 done (the full answer, or "" after a stream), 2 error (a code).
public typealias HypaFMCallback = @convention(c) (Int32, Int32, UnsafePointer<CChar>?) -> Void

private var callback: HypaFMCallback?
private var running: [Int32: Task<Void, Never>] = [:]

// Apple's acceptable-use requirements: no regulated advice, nothing unsuitable for families.
private let instructions = """
You are the storyteller in Hypa, a family-friendly app for learning languages through stories. Keep answers short, \
warm and suitable for all ages. Do not give medical, legal or financial advice.
"""

private func send(_ id: Int32, _ kind: Int32, _ text: String) {
    text.withCString { callback?(id, kind, $0) }
}

@_cdecl("HypaFM_SetCallback")
public func HypaFM_SetCallback(_ cb: HypaFMCallback?) {
    callback = cb
}

/// 0 available, 1 model still downloading, 2 device not eligible, 3 Apple Intelligence off, 4 iOS older than 26.
@_cdecl("HypaFM_Status")
public func HypaFM_Status() -> Int32 {
#if canImport(FoundationModels)
    if #available(iOS 26.0, *) {
        switch SystemLanguageModel.default.availability {
        case .available: return 0
        case .unavailable(.modelNotReady): return 1
        case .unavailable(.appleIntelligenceNotEnabled): return 3
        case .unavailable: return 2
        }
    }
#endif
    return 4
}

/// Answers one prompt in a new session. A stream sends only the new text in each chunk.
@_cdecl("HypaFM_Start")
public func HypaFM_Start(_ id: Int32, _ prompt: UnsafePointer<CChar>, _ stream: Bool) {
    let text = String(cString: prompt)
#if canImport(FoundationModels)
    if #available(iOS 26.0, *), HypaFM_Status() == 0 {
        running[id] = Task { @MainActor in
            defer { running[id] = nil }
            do {
                let session = LanguageModelSession(instructions: instructions)
                if stream {
                    var sent = ""
                    // Each snapshot holds the whole answer so far.
                    for try await snapshot in session.streamResponse(to: text) {
                        let all = snapshot.content
                        guard all.count > sent.count, all.hasPrefix(sent) else { continue }
                        send(id, 0, String(all.dropFirst(sent.count)))
                        sent = all
                    }
                    // A cancelled stream just stops, without an error.
                    send(id, Task.isCancelled ? 2 : 1, Task.isCancelled ? "cancelled" : "")
                } else {
                    send(id, 1, try await session.respond(to: text).content)
                }
            } catch let e as LanguageModelSession.GenerationError {
                send(id, 2, code(e))
            } catch {
                send(id, 2, Task.isCancelled ? "cancelled" : "failed")
            }
        }
        return
    }
#endif
    // Never wait for a model that is not ready: the router uses the cloud instead.
    DispatchQueue.main.async { send(id, 2, "unavailable") }
}

@_cdecl("HypaFM_Cancel")
public func HypaFM_Cancel(_ id: Int32) {
    running.removeValue(forKey: id)?.cancel()
}

#if canImport(FoundationModels)
@available(iOS 26.0, *)
private func code(_ e: LanguageModelSession.GenerationError) -> String {
    switch e {
    case .exceededContextWindowSize: return "too_long"
    case .guardrailViolation: return "guardrail"
    case .unsupportedLanguageOrLocale: return "unsupported_language"
    case .assetsUnavailable: return "unavailable"
    case .rateLimited: return "busy"
    default: return "failed"
    }
}
#endif
