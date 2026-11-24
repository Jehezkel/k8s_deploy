const bindings = new WeakMap();
export function attach(element, receiver) {
    let last = 0;
    const move = event => {
        if (performance.now() - last < 75) return;
        last = performance.now();
        const box = element.getBoundingClientRect();
        const x = (event.clientX - box.left) / box.width * 1000;
        receiver.invokeMethodAsync('SetCursor', (x - 180) / 800 * 1000).catch(() => {});
    };
    element.addEventListener('pointerdown', move);
    bindings.set(element, move);
}
export function detach(element) {
    const handler = bindings.get(element);
    if (handler) element.removeEventListener('pointerdown', handler);
    bindings.delete(element);
}
