// Passed directly to Playwright: query and measure the current DOM in one
// synchronous browser task, never a handle resolved before an inbox refresh.
function measureLivePoster(doc = document) {
    const main=doc.querySelector('#account-hub-inbox .notificationMainCanvas');
    if(!main?.isConnected)return null;
    const canvas=main.querySelector('.notificationCanvas'),poster=canvas?.querySelector('.notificationCanvasImage'),image=poster?.querySelector('img'),timestamp=main.querySelector('time');
    const layers=canvas?[...canvas.querySelectorAll('.notificationCanvasLayer:not(.notificationCanvasImage)')]:[];
    if(!canvas||!poster||!image||!timestamp||!layers.length||!image.complete||image.naturalWidth<=0)return null;
    const nodes=[main,canvas,poster,image,timestamp,...layers];
    if(nodes.some(node=>!node.isConnected))return null;
    const [card,box,imageBox,time]=[canvas,poster,image,timestamp].map(node=>node.getBoundingClientRect());
    const text=layers.map(node=>node.getBoundingClientRect());
    if([card,box,imageBox,time,...text].some(rect=>rect.width<=0||rect.height<=0))return null;
    const style=doc.defaultView.getComputedStyle(image);
    return{posterLeft:Math.abs(box.left-card.left)<1,noOverlap:text.every(rect=>box.right<rect.left),fillsPoster:Math.abs(imageBox.width-box.width)<1,fit:style.objectFit,float:style.float,timestampBelow:time.top>=card.bottom-.5};
}
module.exports={measureLivePoster};
