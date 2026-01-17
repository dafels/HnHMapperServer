// File upload helper for Blazor Server
// Reads file as base64 on the client side to bypass SignalR stream issues

window.readFileAsBase64 = function (inputElement) {
    return new Promise((resolve, reject) => {
        const file = inputElement.files[0];
        if (!file) {
            resolve(null);
            return;
        }

        const reader = new FileReader();

        reader.onload = function (e) {
            // Extract base64 data (remove the data URL prefix)
            const base64 = e.target.result.split(',')[1];
            resolve({
                name: file.name,
                size: file.size,
                data: base64
            });
        };

        reader.onerror = function (e) {
            reject(new Error('Failed to read file: ' + e.target.error));
        };

        reader.readAsDataURL(file);
    });
};
