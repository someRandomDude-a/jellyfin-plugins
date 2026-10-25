export async function waitForGuestState(readState, matches = () => true, {
    attempts = 30,
    wait = () => new Promise(resolve => setTimeout(resolve, 1000)),
} = {}) {
    let state;
    for (let i = 0; i < attempts; i++) {
        state = await readState();
        // Readiness alone can describe the previous stream during a playback transition.
        if (state.ready && matches(state)) return state;
        if (i + 1 < attempts) await wait();
    }
    const { ready, playbackMethod, selectedSubtitleIndex, burnedSubtitleIndex, offset } = state ?? {};
    throw new Error(`Guest stream did not reach the expected ready state: ${JSON.stringify({
        ready, playbackMethod, selectedSubtitleIndex, burnedSubtitleIndex, offset,
    })}`);
}
