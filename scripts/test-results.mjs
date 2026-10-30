// Independent groups can continue; dependent steps inside a group still stop.
export function testResults() {
    const failures = [];
    return {
        async run(name, action) {
            try { await action(); }
            catch (error) {
                failures.push(name);
                // Existing fixture diagnostics remain attached to each failed group.
                console.error('FAIL test group: ' + name);
                console.error(error.message);
            }
        },
        finish() {
            if (failures.length) throw Error('Failed test groups (' + failures.length + '):\n' + failures.map(name => '- ' + name).join('\n'));
        }
    };
}
