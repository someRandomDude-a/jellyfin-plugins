// Each theme/viewport owns different rows. Never mistake a retained row from
// the previous case for a response to the current case's inbox request.
function notificationFixtureId(epoch, index) {
    if (!Number.isSafeInteger(epoch) || epoch < 0 || !Number.isSafeInteger(index) || index < 0) throw Error('Invalid notification fixture identity');
    return `fixture-${epoch}-${index}`;
}
module.exports = { notificationFixtureId };
