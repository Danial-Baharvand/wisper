// Test script for Gemini image injection
// Copy and paste this into the browser console on gemini.google.com

(function() {
    console.log('=== Gemini Image Injection Test ===');
    
    // Create a simple test image (1x1 red pixel PNG)
    const testImageBase64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
    
    // Find input field
    const input = document.querySelector('textarea[placeholder*="Ask Gemini"]') ||
                 document.querySelector('textarea[placeholder*="Enter a prompt"]') ||
                 document.querySelector('.gds-body-l') ||
                 document.querySelector('div[contenteditable="true"]') ||
                 document.querySelector('textarea');
    
    if (!input) {
        console.error('No input field found!');
        return;
    }
    
    console.log('Input field found:', input);
    console.log('Input type:', input.tagName, 'ContentEditable:', input.contentEditable);
    
    // Test 1: Direct Image Injection (Strategy 12)
    console.log('\n--- Test 1: Direct Image Injection ---');
    try {
        const dataUrl = 'data:image/png;base64,' + testImageBase64;
        
        if (input.contentEditable === 'true' || input.tagName === 'DIV') {
            const img = document.createElement('img');
            img.src = dataUrl;
            img.style.maxWidth = '100%';
            img.style.height = 'auto';
            img.style.display = 'block';
            img.style.margin = '10px 0';
            
            input.appendChild(img);
            
            input.dispatchEvent(new InputEvent('input', {
                bubbles: true,
                inputType: 'insertImage',
                data: dataUrl
            }));
            
            console.log('✓ Direct image injection completed');
            console.log('Check if image appears in input area');
        } else {
            console.log('Input is not contenteditable, trying alternative method');
        }
    } catch (e) {
        console.error('Error in direct injection:', e);
    }
    
    // Test 2: Clipboard Paste Event
    console.log('\n--- Test 2: Clipboard Paste Event ---');
    try {
        const byteCharacters = atob(testImageBase64);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
            byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const blob = new Blob([byteArray], { type: 'image/png' });
        const file = new File([blob], 'test.png', { type: 'image/png' });
        
        input.focus();
        input.click();
        
        const dataTransfer = new DataTransfer();
        dataTransfer.items.add(file);
        
        const pasteEvent = new ClipboardEvent('paste', {
            bubbles: true,
            cancelable: true,
            clipboardData: dataTransfer
        });
        
        input.dispatchEvent(pasteEvent);
        
        console.log('✓ Clipboard paste event dispatched');
        console.log('Check if image appears in input area');
    } catch (e) {
        console.error('Error in clipboard paste:', e);
    }
    
    // Test 3: Drag & Drop
    console.log('\n--- Test 3: Drag & Drop ---');
    try {
        const byteCharacters = atob(testImageBase64);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
            byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const blob = new Blob([byteArray], { type: 'image/png' });
        const file = new File([blob], 'test.png', { type: 'image/png' });
        
        input.focus();
        
        const dataTransfer = new DataTransfer();
        dataTransfer.items.add(file);
        dataTransfer.effectAllowed = 'all';
        dataTransfer.dropEffect = 'copy';
        
        input.dispatchEvent(new DragEvent('dragenter', {
            bubbles: true,
            cancelable: true,
            dataTransfer: dataTransfer
        }));
        
        input.dispatchEvent(new DragEvent('dragover', {
            bubbles: true,
            cancelable: true,
            dataTransfer: dataTransfer
        }));
        
        input.dispatchEvent(new DragEvent('drop', {
            bubbles: true,
            cancelable: true,
            dataTransfer: dataTransfer
        }));
        
        console.log('✓ Drag & drop events dispatched');
        console.log('Check if image appears in input area');
    } catch (e) {
        console.error('Error in drag & drop:', e);
    }
    
    // Verification
    setTimeout(() => {
        console.log('\n--- Verification ---');
        const images = input.querySelectorAll('img');
        console.log('Images found in input:', images.length);
        if (images.length > 0) {
            console.log('✓ SUCCESS: Image(s) detected in input area!');
            images.forEach((img, i) => {
                console.log(`  Image ${i + 1}:`, img.src.substring(0, 50) + '...');
            });
        } else {
            console.log('✗ No images detected. Check if Gemini accepts the injection method.');
        }
    }, 1000);
    
    console.log('\n=== Test Complete ===');
    console.log('Check the input area above to see if any images appeared.');
})();
