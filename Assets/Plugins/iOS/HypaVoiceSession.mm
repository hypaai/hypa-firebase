// The audio session's voice chat mode while talking with the old woman (G10, VoiceMode in C#): the system's echo cancellation
// for the built-in microphone and speaker, so she does not hear herself. Back to play-and-record without it afterwards.
#import <AVFoundation/AVFoundation.h>

extern "C" void HypaVoice_SetVoiceChat(bool on)
{
    AVAudioSession *session = [AVAudioSession sharedInstance];
    NSError *error = nil;
    AVAudioSessionCategoryOptions options = AVAudioSessionCategoryOptionDefaultToSpeaker | AVAudioSessionCategoryOptionAllowBluetoothA2DP;
    [session setCategory:AVAudioSessionCategoryPlayAndRecord
                    mode:(on ? AVAudioSessionModeVoiceChat : AVAudioSessionModeDefault)
                 options:options
                   error:&error];
    if (error != nil) NSLog(@"Hypa: voice chat mode not set (%ld).", (long)error.code);
    [session setActive:YES error:nil];
}
