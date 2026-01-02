// Live test script for Gemini image injection
// This simulates what Strategy 0 does: clipboard paste
// Run this in browser console on gemini.google.com/app

(function() {
    console.log('=== Testing Gemini Image Injection ===');
    
    // Create a test image (200x100 blue rectangle with text)
    const canvas = document.createElement('canvas');
    canvas.width = 200;
    canvas.height = 100;
    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#4285f4';
    ctx.fillRect(0, 0, 200, 100);
    ctx.fillStyle = 'white';
    ctx.font = 'bold 16px Arial';
    ctx.textAlign = 'center';
    ctx.fillText('TEST IMAGE', 100, 50);
    
    // Convert to blob
    canvas.toBlob(async (blob) => {
        try {
            // Method 1: Try Clipboard API (modern browsers)
            console.log('Method 1: Using Clipboard API...');
            try {
                await navigator.clipboard.write([
                    new ClipboardItem({ 'image/png': blob })
                ]);
                console.log('✅ Image copied to clipboard via Clipboard API');
                
                // Now focus input and simulate paste
                const input = document.querySelector('textarea[placeholder*="Enter a prompt"]') ||
                             document.querySelector('textarea[placeholder*="Ask Gemini"]') ||
                             document.querySelector('textarea');
                
                if (input) {
                    input.focus();
                    input.click();
                    console.log('✅ Input focused');
                    
                    // Wait a moment then check if image appears
                    setTimeout(() => {
                        // Check for images in the input area
                        const container = input.closest('div') || input.parentElement;
                        const images = container ? container.querySelectorAll('img') : [];
                        const fileInputs = document.querySelectorAll('input[type="file"]');
                        
                        console.log('Images found near input:', images.length);
                        console.log('File inputs found:', fileInputs.length);
                        
                        if (images.length > 0) {
                            console.log('✅ SUCCESS: Image detected in input area!');
                            images.forEach((img, i) => {
                                console.log(`  Image ${i + 1}:`, img.src.substring(0, 50) + '...');
                            });
                        } else {
                            console.log('⚠️ No images detected yet. Try manually pressing Ctrl+V in the input field.');
                            console.log('The image is in your clipboard - paste it manually to verify.');
                        }
                    }, 1000);
                } else {
                    console.log('❌ Input field not found');
                }
            } catch (clipboardError) {
                console.log('❌ Clipboard API failed:', clipboardError.message);
                console.log('Trying alternative method...');
                
                // Method 2: Create file input and set file programmatically
                console.log('Method 2: Using file input programmatic upload...');
                const fileInput = document.createElement('input');
                fileInput.type = 'file';
                fileInput.accept = 'image/*';
                fileInput.style.display = 'none';
                document.body.appendChild(fileInput);
                
                const file = new File([blob], 'test.png', { type: 'image/png' });
                const dataTransfer = new DataTransfer();
                dataTransfer.items.add(file);
                
                // Try to set files
                try {
                    Object.defineProperty(fileInput, 'files', {
                        writable: true,
                        value: dataTransfer.files
                    });
                    
                    const changeEvent = new Event('change', { bubbles: true });
                    fileInput.dispatchEvent(changeEvent);
                    
                    if (fileInput.files && fileInput.files.length > 0) {
                        console.log('✅ File set on input:', fileInput.files[0].name);
                        console.log('⚠️ Note: This may not trigger Gemini\'s upload handler');
                    }
                } catch (e) {
                    console.log('❌ File input method failed:', e.message);
                }
            }
        } catch (e) {
            console.error('❌ Error:', e);
        }
    }, 'image/png');
    
    console.log('=== Test script running ===');
    console.log('Check the console and the Gemini input area for results.');
})();
